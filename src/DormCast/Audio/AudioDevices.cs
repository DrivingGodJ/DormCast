using NAudio.CoreAudioApi;

namespace DormCast.Audio;

public sealed record AudioDeviceChoice(string Id, string Name)
{
    public bool IsVirtual => Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static List<AudioDeviceChoice> RenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceChoice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        { using (device) result.Add(new(device.ID, device.FriendlyName)); }
        return result;
    }
    public static string? DefaultRenderId()
    {
        try { using var e = new MMDeviceEnumerator(); using var device = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); return device.ID; }
        catch { return null; }
    }
    public static AudioDeviceChoice? FindVirtualCaptureDevice(IEnumerable<AudioDeviceChoice>? devices = null)
    {
        var list = devices?.ToList() ?? RenderDevices();
        return list.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => d.IsVirtual);
    }
    public static List<AudioDeviceChoice> PhysicalOutputDevices(IEnumerable<AudioDeviceChoice>? devices = null) =>
        (devices ?? RenderDevices()).Where(d => !d.IsVirtual).ToList();
    public static AudioDeviceChoice? ResolvePhysicalOutput(string? preferredId, IEnumerable<AudioDeviceChoice>? devices = null, string? defaultId = null)
    {
        var physical = PhysicalOutputDevices(devices);
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var exact = physical.FirstOrDefault(d => d.Id == preferredId);
            if (exact != null) return exact;
        }
        var activeDefault = defaultId ?? DefaultRenderId();
        return physical.FirstOrDefault(d => d.Id == activeDefault)
            ?? physical.FirstOrDefault(d => d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
            ?? physical.FirstOrDefault();
    }
    public static void ValidateRoute(string? captureId, string? outputId)
    {
        if (string.IsNullOrWhiteSpace(captureId)) throw new ArgumentException("未检测到虚拟声卡（VB-CABLE），无法启动广播。请先安装虚拟声卡后再重试。");
        if (string.IsNullOrWhiteSpace(outputId)) throw new ArgumentException("请选择本机实际输出设备（扬声器或耳机）。");
        if (captureId == outputId) throw new ArgumentException("采集设备与主机扬声器必须不同，避免声音被重复采集形成回授。");
        var devices = RenderDevices();
        if (!devices.Any(d => d.Id == captureId)) throw new ArgumentException("虚拟声卡不可用，请确认已正确安装 VB-CABLE 后重试。");
        if (!devices.Any(d => d.Id == outputId)) throw new ArgumentException("所选输出设备已不可用，请刷新设备列表。");
        if (!devices.First(d => d.Id == captureId).IsVirtual || devices.First(d => d.Id == outputId).IsVirtual) throw new ArgumentException("必须使用虚拟声卡采集并选择实际扬声器/耳机输出。");
    }
}
