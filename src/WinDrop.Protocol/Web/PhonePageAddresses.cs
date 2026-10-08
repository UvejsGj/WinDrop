using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinDrop.Protocol.Web;

/// <summary>Which of this PC's addresses to put in the phone page's QR code.</summary>
public static class PhonePageAddresses
{
    /// <summary>
    /// IPv4 addresses a phone on the same network could reach, best first.
    ///
    /// IPv4 because the address ends up in a QR code and a phone's address bar, where a
    /// scoped IPv6 link-local address would not survive. Ranked: an interface with a default
    /// gateway first, since virtual switches (WSL, Hyper-V, VM host-only networks) have none
    /// and their addresses are unreachable from a phone; then Wi-Fi before Ethernet before
    /// anything else, since the phone is on Wi-Fi; then private ranges before public ones.
    /// Self-assigned 169.254 addresses mean DHCP failed, and are left out.
    /// </summary>
    public static IReadOnlyList<IPAddress> Find()
    {
        var found = new List<(IPAddress Address, int Rank)>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties properties = nic.GetIPProperties();

            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address is { AddressFamily: AddressFamily.InterNetwork } gateway && !gateway.Equals(IPAddress.Any));

            int kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 10,
                _ => 20,
            };

            foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
            {
                IPAddress address = info.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;

                byte[] bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;

                found.Add((address, (hasGateway ? 0 : 100) + kind + (IsPrivate(bytes) ? 0 : 5)));
            }
        }

        return found.OrderBy(f => f.Rank).Select(f => f.Address).Distinct().ToList();
    }

    private static bool IsPrivate(byte[] b) =>
        b[0] == 10
        || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        || (b[0] == 192 && b[1] == 168);
}
