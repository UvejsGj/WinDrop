using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;

namespace WinDrop.App;

public partial class MainWindow : Window
{
    private const double SelectionPreviewBox = 84;
    private const double ConsentPreviewBox = 116;

    private readonly ObservableCollection<PeerViewModel> _peers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _files = [];

    // One sheet, so one prompt at a time even when transfers overlap.
    private readonly SemaphoreSlim _consentGate = new(1, 1);

    private InfraWifiTransport? _transport;
    private X509Certificate2? _certificate;
    private TaskCompletionSource<bool>? _pendingConsent;
    private string _downloadDirectory = "";

    // Bumped on every new selection. Previews are slow and arrive late; one that belongs
    // to a selection the user has already replaced is dropped rather than shown.
    private int _selection;
    private Task<byte[]?> _fileIcon = Task.FromResult<byte[]?>(null);

    // Read from the consent path, which is not on the UI thread, so it is cached here
    // rather than asked of the visual tree at the time.
    private double _dpiScale = 1;

    public MainWindow()
    {
        InitializeComponent();
        PeerList.ItemsSource = _peers;

        _peers.CollectionChanged += (_, _) =>
            EmptyState.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The HWND does not exist until SourceInitialized, and applying the backdrop
        // after first render leaves a visible flash of the fallback colour.
        SourceInitialized += (_, _) =>
        {
            WindowBackdrop.Apply(this);
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _shutdown.Cancel();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
    }

    private int PixelsFor(double box) => (int)Math.Ceiling(box * _dpiScale);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

        Directory.CreateDirectory(_downloadDirectory);

        // Paths on the command line are a selection. That is how both dropping files onto
        // the exe and Explorer's "Send to" hand them over.
        string[] launched = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (launched.Length > 0) SetFiles(launched);

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
                // Each connection gets its own task. Transfers can overlap; only the
                // consent prompt is serialised, since there is one sheet to show it in.
                _ = ServeAsync(receiver, raw, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(AirDropReceiver receiver, Stream raw, CancellationToken ct)
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

    /// <summary>
    /// Bridges the protocol's consent callback to the UI. This is the security boundary
    /// of the whole protocol — TLS authenticates nobody — so it blocks the transfer until
    /// a person actually answers, rather than defaulting either way on a timeout.
    /// </summary>
    private async Task<bool> AskUserAsync(AirDropAskRequest request, CancellationToken ct)
    {
        await _consentGate.WaitAsync(ct);

        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Ready before the sheet opens, so the prompt appears whole instead of an image
            // popping in once the user has started reading. Every step is time-boxed, so a
            // slow or hostile preview can hold the prompt back by seconds, not indefinitely.
            FilePreview? preview = await PreviewForAskAsync(request);

            await Dispatcher.InvokeAsync(() =>
            {
                _pendingConsent = completion;

                string names = string.Join(", ", request.Files.Select(f => f.FileName));

                ConsentTitle.Text = $"{request.SenderComputerName} would like to share";
                ConsentDetail.Text = request.Files.Count == 1
                    ? names
                    : $"{request.Files.Count} items · {names}";

                ConsentPreview.ItemsSource = preview is null ? null : PreviewItem.Stack([preview], ConsentPreviewBox);
                ConsentPreview.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;

                ConsentSheet.Visibility = Visibility.Visible;
            });

            await using (ct.Register(() => completion.TrySetResult(false)))
                return await completion.Task;
        }
        finally
        {
            _consentGate.Release();
        }
    }

    /// <summary>
    /// The sender's own preview when there is one we are willing to decode, otherwise the
    /// Windows icon for the first item's type. One image either way: the protocol carries
    /// a single preview for the whole request.
    /// </summary>
    private async Task<FilePreview?> PreviewForAskAsync(AirDropAskRequest request)
    {
        int pixels = PixelsFor(ConsentPreviewBox);

        if (request.FileIcon is { } icon && await FileIconCodec.DecodeAsync(icon, pixels) is { } decoded)
            return decoded;

        // Either no icon was sent, or it was one we decline — which for opendrop is every
        // icon, since it only ever sends JPEG 2000.
        if (request.Files.Count == 0) return null;

        AirDropFileEntry first = request.Files[0];
        return await WithinAsync(ShellPreview.ForTypeAsync(first.FileName, first.IsDirectory), seconds: 3);
    }

    private void OnAccept(object sender, RoutedEventArgs e) => ResolveConsent(true);

    private void OnDecline(object sender, RoutedEventArgs e) => ResolveConsent(false);

    private void ResolveConsent(bool accepted)
    {
        ConsentSheet.Visibility = Visibility.Collapsed;
        ConsentPreview.ItemsSource = null;

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
        // Snapshot the selection, icon included, before the first await: the user is free
        // to pick different files while this one is still in flight.
        var outgoing = _files.Select(AirDropOutgoingFile.FromPath).ToList();
        Task<byte[]?> fileIcon = _fileIcon;
        long total = outgoing.Sum(f => f.TotalBytes);

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
                outgoing.Select(f => f.ToEntry()).ToList(),
                FileIcon: await fileIcon);

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
        string[] files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        int missing = paths.Count() - files.Length;

        _files.Clear();
        _files.AddRange(files);

        int selection = ++_selection;

        PreviewStack.ItemsSource = null;
        PreviewStack.Visibility = Visibility.Collapsed;

        foreach (PeerViewModel peer in _peers)
        {
            peer.State = PeerState.Idle;
            peer.Progress = 0;
            peer.Status = "";
        }

        if (_files.Count == 0)
        {
            _fileIcon = Task.FromResult<byte[]?>(null);
            ChooseButton.Content = "Choose";
            FilesHeadline.Text = "Drop files here";
            FilesDetail.Text = "folders welcome";
            return;
        }

        ChooseButton.Content = "Change";

        // The protocol carries one preview for the whole request, taken from the first
        // item — the same choice opendrop makes. Started now rather than at send time, so
        // it is ready by the time someone taps a peer.
        _fileIcon = File.Exists(_files[0])
            ? FileIconCodec.CreateAsync(_files[0])
            : Task.FromResult<byte[]?>(null);

        _ = ShowSelectionPreviewAsync(selection, _files.Take(3).ToList());

        long bytes = _files.Sum(f => AirDropOutgoingFile.FromPath(f).TotalBytes);

        FilesHeadline.Text = _files.Count == 1
            ? Path.GetFileName(_files[0].TrimEnd(Path.DirectorySeparatorChar))
            : $"{_files.Count} items";

        FilesDetail.Text = missing > 0
            ? $"{FormatSize(bytes)} · {missing} item(s) missing · tap someone to send"
            : $"{FormatSize(bytes)} · tap someone to send";
    }

    private async Task ShowSelectionPreviewAsync(int selection, IReadOnlyList<string> paths)
    {
        int pixels = PixelsFor(SelectionPreviewBox);

        FilePreview?[] loaded = await Task.WhenAll(
            paths.Select(p => WithinAsync(ShellPreview.ForPathAsync(p, pixels), seconds: 10)));

        if (selection != _selection) return;

        var previews = loaded.OfType<FilePreview>().ToList();
        if (previews.Count == 0) return;

        PreviewStack.ItemsSource = PreviewItem.Stack(previews, SelectionPreviewBox);
        PreviewStack.Visibility = Visibility.Visible;
    }

    /// <summary>A preview is decoration: late or broken means none, never an error.</summary>
    private static async Task<FilePreview?> WithinAsync(Task<FilePreview?> preview, double seconds)
    {
        try
        {
            return await preview.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ---- chrome ------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;
}
