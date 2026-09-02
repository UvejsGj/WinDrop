using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;

namespace WinDrop.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PeerViewModel> _peers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _files = [];

    private InfraWifiTransport? _transport;
    private X509Certificate2? _certificate;
    private TaskCompletionSource<bool>? _pendingConsent;
    private string _downloadDirectory = "";

    public MainWindow()
    {
        InitializeComponent();
        PeerList.ItemsSource = _peers;

        _peers.CollectionChanged += (_, _) =>
            EmptyState.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
        Closed += (_, _) => _shutdown.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

        Directory.CreateDirectory(_downloadDirectory);

        try
        {
            _certificate = AirDropCertificate.CreateSelfSigned(Environment.MachineName);
            _transport = new InfraWifiTransport();

            var flags = AirDropReceiverFlags.SupportsDvZip | AirDropReceiverFlags.SupportsMixedTypes;
            string instance = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}";
            var record = new AirDropServiceRecord(instance, AirDropServiceRecord.DefaultPort, flags);

            await _transport.AdvertiseAsync(record, _shutdown.Token);

            _ = BrowseLoopAsync(_shutdown.Token);
            _ = ReceiveLoopAsync(record, flags, _shutdown.Token);

            SetStatus($"Discoverable as {instance} · saving to {_downloadDirectory}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start: {ex.Message}");
        }
    }

    // ---- discovery ---------------------------------------------------------

    private async Task BrowseLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (AirDropPeer peer in _transport!.BrowseAsync(ct))
            {
                AirDropPeer captured = peer;

                await Dispatcher.InvokeAsync(() =>
                {
                    // The transport can surface the same instance more than once as
                    // records arrive piecemeal; keep one tile per instance.
                    if (_peers.Any(p => p.Peer.InstanceName == captured.InstanceName)) return;

                    _peers.Add(new PeerViewModel(captured));
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => SetStatus($"Discovery stopped: {ex.Message}"));
        }
    }

    // ---- receiving ---------------------------------------------------------

    private async Task ReceiveLoopAsync(AirDropServiceRecord record, AirDropReceiverFlags flags, CancellationToken ct)
    {
        var receiver = new AirDropReceiver(new AirDropReceiverOptions
        {
            ComputerName = Environment.MachineName,
            DownloadDirectory = _downloadDirectory,
            Flags = flags,
            ConsentHandler = AskUserAsync,
        });

        try
        {
            await foreach (Stream raw in _transport!.AcceptAsync(record.Port, ct))
            {
                try
                {
                    await using var ssl = await AirDropTls.AuthenticateAsServerAsync(raw, _certificate!, ct);
                    AirDropTransferResult? result = await receiver.HandleConnectionAsync(ssl, ct);

                    if (result is not null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                            SetStatus($"Received {result.Files.Count} file(s), {result.TotalBytes:N0} bytes"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() => SetStatus($"Transfer failed: {ex.Message}"));
                }
                finally
                {
                    await raw.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Bridges the protocol's consent callback to the UI. This is the security boundary
    /// of the whole protocol — TLS authenticates nobody — so it blocks the transfer until
    /// a person actually answers, rather than defaulting either way on a timeout.
    /// </summary>
    private Task<bool> AskUserAsync(AirDropAskRequest request, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.InvokeAsync(() =>
        {
            _pendingConsent = completion;

            string names = string.Join(", ", request.Files.Select(f => f.FileName));

            ConsentTitle.Text = $"{request.SenderComputerName} would like to share";
            ConsentDetail.Text = request.Files.Count == 1
                ? names
                : $"{request.Files.Count} items · {names}";

            ConsentSheet.Visibility = Visibility.Visible;
        });

        ct.Register(() => completion.TrySetResult(false));
        return completion.Task;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => ResolveConsent(true);

    private void OnDecline(object sender, RoutedEventArgs e) => ResolveConsent(false);

    private void ResolveConsent(bool accepted)
    {
        ConsentSheet.Visibility = Visibility.Collapsed;
        _pendingConsent?.TrySetResult(accepted);
        _pendingConsent = null;

        SetStatus(accepted ? "Accepted — receiving…" : "Declined");
    }

    // ---- sending -----------------------------------------------------------

    private async void OnPeerClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PeerViewModel peer }) return;

        if (_files.Count == 0)
        {
            SetStatus("Choose files first, or drop them onto the window");
            return;
        }

        if (peer.State == PeerState.Sending) return;

        await SendToAsync(peer);
    }

    private async Task SendToAsync(PeerViewModel peer)
    {
        var outgoing = _files.Select(AirDropOutgoingFile.FromPath).ToList();
        long total = outgoing.Sum(f => new FileInfo(f.LocalPath).Length);

        peer.State = PeerState.Sending;
        peer.Progress = 0;
        peer.Status = "Waiting…";

        try
        {
            await using Stream raw = await _transport!.ConnectAsync(peer.Peer, _shutdown.Token);
            await using var ssl = await AirDropTls.AuthenticateAsClientAsync(raw, _certificate!, _shutdown.Token);
            await using var session = new AirDropSenderSession(ssl, peer.Peer.Flags);

            await session.DiscoverAsync(_shutdown.Token);

            var ask = new AirDropAskRequest(
                Environment.MachineName,
                "Windows",
                Guid.NewGuid().ToString(),
                AirDropAskRequest.FinderBundleId,
                outgoing.Select(f => f.ToEntry()).ToList());

            if (!await session.AskAsync(ask, _shutdown.Token))
            {
                peer.State = PeerState.Declined;
                peer.Status = "Declined";
                peer.Progress = 0;
                return;
            }

            peer.Status = "Sending…";

            var progress = new Progress<long>(sent =>
            {
                if (total > 0) peer.Progress = Math.Min(1.0, (double)sent / total);
            });

            await session.UploadAsync(outgoing, progress, _shutdown.Token);

            peer.Progress = 1;
            peer.State = PeerState.Sent;
            peer.Status = "Sent";
        }
        catch (Exception ex)
        {
            peer.State = PeerState.Failed;
            peer.Status = "Failed";
            peer.Progress = 0;
            SetStatus($"Send failed: {ex.Message}");
        }
    }

    // ---- file selection ----------------------------------------------------

    private void OnChooseFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to send" };
        if (dialog.ShowDialog(this) != true) return;

        SetFiles(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            SetFiles(paths);
    }

    private void SetFiles(IEnumerable<string> paths)
    {
        // Directories would need to be walked into cpio entries with their own paths;
        // that is real work, so refuse them clearly rather than silently dropping them.
        string[] files = paths.Where(File.Exists).ToArray();
        int rejected = paths.Count() - files.Length;

        _files.Clear();
        _files.AddRange(files);

        foreach (PeerViewModel peer in _peers)
        {
            peer.State = PeerState.Idle;
            peer.Progress = 0;
            peer.Status = "";
        }

        if (_files.Count == 0)
        {
            FilesHeadline.Text = "Drop files here to send";
            FilesDetail.Text = "or click to choose";
            return;
        }

        long bytes = _files.Sum(f => new FileInfo(f).Length);

        FilesHeadline.Text = _files.Count == 1
            ? Path.GetFileName(_files[0])
            : $"{_files.Count} files";

        FilesDetail.Text = rejected > 0
            ? $"{bytes:N0} bytes · {rejected} folder(s) skipped · tap someone to send"
            : $"{bytes:N0} bytes · tap someone to send";
    }

    // ---- chrome ------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;
}
