using NAudio.CoreAudioApi;

namespace DormCast.Audio;

public enum CaptureEndpointFlow { RenderLoopback, CaptureInput }

public sealed record AudioDeviceChoice(string Id, string Name, CaptureEndpointFlow Flow = CaptureEndpointFlow.RenderLoopback)
{
    public bool IsVirtual => Flow == CaptureEndpointFlow.RenderLoopback &&
        (Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase));
    public bool IsCaptureInput => Flow == CaptureEndpointFlow.CaptureInput;
    public override string ToString() => Flow == CaptureEndpointFlow.CaptureInput
        ? $"[输入] {Name}"
        : IsVirtual
            ? $"[虚拟声卡] {Name}"
            : Name;
}

public static class AudioDevices
{
    public static List<AudioDeviceChoice> RenderDevices() => Enumerate(DataFlow.Render, CaptureEndpointFlow.RenderLoopback);
    public static List<AudioDeviceChoice> InputDevices() => Enumerate(DataFlow.Capture, CaptureEndpointFlow.CaptureInput);
    static List<AudioDeviceChoice> Enumerate(DataFlow flow, CaptureEndpointFlow kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceChoice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        { using (device) result.Add(new(device.ID, device.FriendlyName, kind)); }
        return result;
    }
    public static string? DefaultRenderId()
    {
        try { using var e = new MMDeviceEnumerator(); using var device = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); return device.ID; }
        catch { return null; }
    }
    public static AudioDeviceChoice? FindVirtualCaptureDevice(IEnumerable<AudioDeviceChoice>? devices = null)
    {
        var list = (devices ?? RenderDevices()).Where(d => d.Flow == CaptureEndpointFlow.RenderLoopback).ToList();
        return list.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => d.IsVirtual);
    }
    public static List<AudioDeviceChoice> CaptureDevices(IEnumerable<AudioDeviceChoice>? renderDevices = null, IEnumerable<AudioDeviceChoice>? inputDevices = null)
    {
        var renders = (renderDevices ?? RenderDevices()).Where(d => d.IsVirtual)
            .OrderByDescending(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var inputs = (inputDevices ?? InputDevices())
            .Where(d => d.Flow == CaptureEndpointFlow.CaptureInput)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        return renders.Concat(inputs).ToList();
    }
    public static AudioDeviceChoice? ResolveCaptureDevice(string? preferredId, IEnumerable<AudioDeviceChoice>? renderDevices = null, IEnumerable<AudioDeviceChoice>? inputDevices = null)
    {
        var renders = (renderDevices ?? RenderDevices()).ToList();
        var inputs = (inputDevices ?? InputDevices()).ToList();
        var captures = CaptureDevices(renders, inputs);
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var exact = captures.FirstOrDefault(d => d.Id == preferredId);
            if (exact != null) return exact;
        }
        return FindVirtualCaptureDevice(renders);
    }
    public static List<AudioDeviceChoice> PhysicalOutputDevices(IEnumerable<AudioDeviceChoice>? devices = null) =>
        (devices ?? RenderDevices()).Where(d => d.Flow == CaptureEndpointFlow.RenderLoopback && !d.IsVirtual).ToList();
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
    public static void ValidateRoute(string? captureId, string? outputId) =>
        ValidateRoute(captureId, outputId, hostPlayback: true);
    public static AudioDeviceChoice ValidateRoute(string? captureId, string? outputId, bool hostPlayback,
        IEnumerable<AudioDeviceChoice>? renderDevices = null, IEnumerable<AudioDeviceChoice>? inputDevices = null)
    {
        if (string.IsNullOrWhiteSpace(captureId)) throw new ArgumentException("请安装虚拟声卡（VB-CABLE）或选择一个音频采集设备。");
        var renders = (renderDevices ?? RenderDevices()).ToList();
        var inputs = (inputDevices ?? InputDevices()).ToList();
        var capture = CaptureDevices(renders, inputs).FirstOrDefault(d => d.Id == captureId)
            ?? throw new ArgumentException("所选采集设备已不可用，请刷新设备列表。");
        if (hostPlayback)
        {
            if (string.IsNullOrWhiteSpace(outputId)) throw new ArgumentException("电脑发声时必须选择本机真实输出设备（扬声器或耳机）。");
            if (captureId == outputId) throw new ArgumentException("采集设备与真实输出设备必须不同，避免声音被重复采集形成回授。");
            var output = renders.FirstOrDefault(d => d.Id == outputId)
                ?? throw new ArgumentException("所选真实输出设备已不可用，请刷新设备列表。");
            if (output.IsVirtual) throw new ArgumentException("真实输出设备必须选择实际扬声器或耳机，不能选择虚拟声卡。");
        }
        return capture;
    }
}
