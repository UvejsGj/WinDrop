using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinDrop.App.Glass;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;

namespace WinDrop.App;

public partial class MainWindow : Window
{
    private const double SelectionPreviewBox = 50;
    private const double ConsentPreviewBox = 120;

    // The radar's centre in window coordinates. See the layer notes in MainWindow.xaml.
    private const double RadarX = 220;
    private const double RadarY = 300;
    private const double PeerOrbit = 150;

    // Top of the orbit first, then spreading down both sides, so the first few people land
    // where the eye already is and nobody sits on top of this device's own label.
    private static readonly double[] PeerAngles = [-90, -142, -38, 180, 0, 142, 38];

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

    private CancellationTokenSource? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        PeerList.ItemsSource = _peers;
        LocalName.Text = Environment.MachineName;

        _peers.CollectionChanged += (_, _) =>
        {
            EmptyState.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LayoutPeers();
        };

        // The HWND does not exist until SourceInitialized.
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
        StartSceneMotion();

        if (!LiquidGlassEffect.IsAvailable)
            SetStatus($"Plain glass: {LiquidGlassEffect.UnavailableReason}");

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

            DiscoverabilityText.Text = "Visible to everyone nearby";
        }
        catch (Exception ex)
        {
            DiscoverabilityText.Text = "Not discoverable";
            SetStatus($"Could not start: {ex.Message}");
        }
    }

    // ---- scene -------------------------------------------------------------

    private void StartSceneMotion()
    {
        Drift(AmbientA, 80, 60, 17);
        Drift(AmbientB, -70, -80, 21);
        Drift(AmbientC, 60, -50, 26);

        var pulses = new[] { PulseA, PulseB, PulseC };

        for (int i = 0; i < pulses.Length; i++)
        {
            var scale = new ScaleTransform(0.2, 0.2);
            pulses[i].RenderTransform = scale;

            var begin = TimeSpan.FromSeconds(i * 1.5);
            var duration = TimeSpan.FromSeconds(4.5);

            var grow = new DoubleAnimation(0.18, 1.0, duration)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

            pulses[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, duration)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    private static void Drift(UIElement element, double dx, double dy, double seconds)
    {
        var offset = new TranslateTransform();
        element.RenderTransform = offset;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
        });

        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.37))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
        });
    }

    private void OnPointerMoved(object sender, MouseEventArgs e) =>
        GlassPanel.LightPosition = e.GetPosition(Scene);

    private void LayoutPeers()
    {
        for (int i = 0; i < _peers.Count; i++)
        {
            bool firstOrbit = i < PeerAngles.Length;
            double radius = firstOrbit ? PeerOrbit : PeerOrbit + 50;
            double angle = (PeerAngles[i % PeerAngles.Length] + (firstOrbit ? 0 : 22)) * Math.PI / 180;

            // The tile is 120 wide with the bubble's centre 42 down from its top.
            _peers[i].X = RadarX + radius * Math.Cos(angle) - 60;
            _peers[i].Y = RadarY + radius * Math.Sin(angle) - 42;
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
                    SetStatus($"Saved {result.Files.Count} item(s) to Downloads › WinDrop"));
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

                ConsentTitle.Text = request.SenderComputerName;
                ConsentDetail.Text = request.Files.Count == 1
                    ? $"wants to share “{names}”"
                    : $"wants to share {request.Files.Count} items · {names}";

                ConsentPreview.ItemsSource = preview is null ? null : PreviewItem.Stack([preview], ConsentPreviewBox);
                ConsentPreview.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;

                ShowConsentSheet();
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
        _pendingConsent?.TrySetResult(accepted);
        _pendingConsent = null;

        HideConsentSheet();
        SetStatus(accepted ? "Receiving…" : "Declined");
    }

    private void ShowConsentSheet()
    {
        ConsentSheet.Visibility = Visibility.Visible;

        ConsentScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));

        ConsentSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(480, 0, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut },
        });
    }

    private void HideConsentSheet()
    {
        var slide = new DoubleAnimation(520, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        slide.Completed += (_, _) =>
        {
            // Another request may have opened the sheet again while this one slid away.
            if (_pendingConsent is not null) return;

            ConsentSheet.Visibility = Visibility.Collapsed;
            ConsentPreview.ItemsSource = null;
        };

        ConsentSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        ConsentScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
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
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to share" };
        if (dialog.ShowDialog(this) != true) return;

        SetFiles(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) SetDropTargetActive(true);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires every time the pointer crosses from one child element into
        // another. Only a pointer that has actually left the window ends the highlight.
        Point p = e.GetPosition(this);
        if (p.X > 0 && p.Y > 0 && p.X < ActualWidth && p.Y < ActualHeight) return;

        SetDropTargetActive(false);
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        SetDropTargetActive(false);

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            SetFiles(paths);
    }

    private void SetDropTargetActive(bool active)
    {
        var spring = new DoubleAnimation(active ? 1.04 : 1.0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
        };

        TrayScale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
        TrayScale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
        Tray.BeginAnimation(GlassPanel.HighlightProperty, new DoubleAnimation(active ? 1 : 0, TimeSpan.FromMilliseconds(250)));
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
        TrayPlaceholder.Visibility = Visibility.Visible;

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
            FilesHeadline.Text = "Drop files to share";
            FilesDetail.Text = "or choose them from this PC";
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
            ? $"{FormatSize(bytes)} · {missing} missing · tap someone to send"
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
        TrayPlaceholder.Visibility = Visibility.Collapsed;
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

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Shows a short message in a glass capsule that slides in and fades away.</summary>
    private void SetStatus(string text)
    {
        ToastText.Text = text;

        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(480))
        {
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut },
        });

        _toastTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _toastTimer = timer;

        _ = HideToastAsync(timer.Token);
    }

    private async Task HideToastAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)));
    }
}
