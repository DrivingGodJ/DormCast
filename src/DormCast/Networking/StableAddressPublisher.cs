using DormCast.Hosting;
using Makaretu.Dns;

namespace DormCast.Networking;

public sealed class StableAddressPublisher : IDisposable
{
    MulticastService? _multicast;
    ServiceDiscovery? _discovery;
    ServiceProfile? _profile;
    public string Status { get; private set; } = "尚未发布固定地址";
    public void Start(LanInterface nic, JoinIdentity identity, int port)
    {
        try
        {
            _multicast = new MulticastService(interfaces => interfaces.Where(n => n.Id == nic.Id)) { UseIpv6 = false };
            _discovery = new ServiceDiscovery(_multicast);
            _profile = new ServiceProfile(identity.HostName.Split('.')[0] + $"-{port}", "_http._tcp", (ushort)port, [nic.Address]);
            _profile.HostName = identity.HostName;
            foreach (var record in _profile.Resources)
            {
                if (record is AddressRecord) record.Name = identity.HostName;
                if (record is SRVRecord srv) srv.Target = identity.HostName;
            }
            _multicast.Start(); _discovery.Advertise(_profile); _discovery.Announce(_profile);
            Status = "固定 .local 地址已发布";
        }
        catch (Exception ex) { Status = "固定名称暂不可用，请使用 IP 备用链接"; AppLog.Write("mDNS fixed address unavailable", ex); Dispose(); }
    }
    public void Dispose()
    {
        try { if (_profile != null) _discovery?.Unadvertise(_profile); } catch (Exception ex) { AppLog.Write("mDNS unadvertise", ex); }
        _discovery?.Dispose(); _multicast?.Dispose(); _discovery = null; _multicast = null; _profile = null;
    }
}
