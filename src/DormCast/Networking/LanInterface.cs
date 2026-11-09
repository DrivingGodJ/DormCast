using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DormCast.Networking;

public sealed record LanInterface(string Id, string Name, IPAddress Address, IPAddress Mask, bool HasGateway, bool IsPhysical)
{
    public override string ToString() => $"{Name}  ·  {Address}";
    public bool Contains(IPAddress? remote)
    {
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        if (remote?.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(remote)) return false;
        var ip = Address.GetAddressBytes(); var other = remote.GetAddressBytes(); var mask = Mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) if ((ip[i] & mask[i]) != (other[i] & mask[i])) return false;
        return true;
    }
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
    public static List<LanInterface> Discover() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => IsPrivate(a.Address) && a.IPv4Mask != null)
            .Select(a => new LanInterface(n.Id, n.Name, a.Address, a.IPv4Mask,
                n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)),
                n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(n => n.HasGateway).ThenByDescending(n => n.IsPhysical).ThenBy(n => n.Name).ToList();
    public bool IsAvailable() => Discover().Any(n => n.Id == Id && n.Address.Equals(Address));
}
