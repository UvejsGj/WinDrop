using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinDrop.Protocol.Discovery;

namespace WinDrop.App;

public enum PeerState { Idle, Sending, Sent, Failed, Declined }

/// <summary>
/// One peer tile. Mirrors AirDrop's share sheet: a circular avatar with initials, the
/// name beneath, and — during a transfer — a ring that fills around the avatar, which is
/// the one bit of motion that makes the thing feel like AirDrop rather than a file list.
/// </summary>
public sealed class PeerViewModel : INotifyPropertyChanged
{
    private PeerState _state = PeerState.Idle;
    private double _progress;
    private string _status = "";

    public PeerViewModel(AirDropPeer peer)
    {
        Peer = peer;
        DisplayName = Prettify(peer.InstanceName);
        Initials = InitialsFor(DisplayName);
    }

    public AirDropPeer Peer { get; }
    public string DisplayName { get; }
    public string Initials { get; }

    public string Detail => $"{Peer.EndPoint.Address}";

    public PeerState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Notify();
            Notify(nameof(RingBrush));
            Notify(nameof(ShowRing));
        }
    }

    /// <summary>0..1. Drives the arc around the avatar.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.0005) return;
            _progress = value;
            Notify();
        }
    }

    public string Status
    {
        get => _status;
        set { _status = value; Notify(); }
    }

    public bool ShowRing => State is PeerState.Sending or PeerState.Sent;

    /// <summary>
    /// State is signalled by value, not hue — there is no green or red to reach for.
    /// Success is full white, in progress slightly held back, and a failure recedes
    /// rather than shouting. The status text carries the words; this carries the weight.
    /// </summary>
    public Brush RingBrush => State switch
    {
        PeerState.Sent => Frozen(1.0),
        PeerState.Failed or PeerState.Declined => Frozen(0.28),
        _ => Frozen(0.8),
    };

    private static Brush Frozen(double opacity)
    {
        var brush = new SolidColorBrush(Colors.White) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Instance names carry a disambiguating suffix that is noise to a human reader —
    /// ours is "MACHINE-a0ddaf". Strip it for display but keep the original for matching.
    /// </summary>
    private static string Prettify(string instanceName)
    {
        int dash = instanceName.LastIndexOf('-');

        if (dash > 0 && instanceName.Length - dash - 1 is >= 4 and <= 8)
            return instanceName[..dash];

        return instanceName;
    }

    private static string InitialsFor(string name)
    {
        string[] parts = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant(),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
