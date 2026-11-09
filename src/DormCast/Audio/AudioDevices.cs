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
    public static void ValidateRoute(string? captureId, string? outputId)
    {
        if (string.IsNullOrWhiteSpace(captureId) || string.IsNullOrWhiteSpace(outputId)) throw new ArgumentException("主机同步播放需要选择虚拟采集设备和实际扬声器。");
        if (captureId == outputId) throw new ArgumentException("采集设备与主机扬声器必须不同，避免声音被重复采集形成回授。");
        var devices = RenderDevices();
        if (!devices.Any(d => d.Id == captureId) || !devices.Any(d => d.Id == outputId)) throw new ArgumentException("所选音频设备已不可用，请刷新设备列表。");
        if (!devices.First(d => d.Id == captureId).IsVirtual || devices.First(d => d.Id == outputId).IsVirtual) throw new ArgumentException("主机同步播放需要虚拟声卡采集、实际扬声器输出，请重新选择设备。");
    }
}
