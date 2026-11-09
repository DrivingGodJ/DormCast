using NAudio.CoreAudioApi;

namespace DormCast.Audio;

public sealed record SpeakerVolumeState(string DeviceId, string Name, float Level, bool Muted);

public static class SpeakerVolume
{
    public static SpeakerVolumeState? Read(string? preferredId)
    {
        var devices = AudioDevices.RenderDevices();
        var defaultId = AudioDevices.DefaultRenderId();
        var selected = devices.FirstOrDefault(d => d.Id == preferredId && !d.IsVirtual)
            ?? devices.FirstOrDefault(d => d.Id == defaultId && !d.IsVirtual)
            ?? devices.FirstOrDefault(d => !d.IsVirtual && d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => !d.IsVirtual);
        if (selected == null) return null;
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(selected.Id);
        return new SpeakerVolumeState(selected.Id, selected.Name, device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
    }
    public static void Set(string deviceId, float level)
    {
        if (!float.IsFinite(level) || level is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(level));
        var selected = AudioDevices.RenderDevices().FirstOrDefault(d => d.Id == deviceId && !d.IsVirtual)
            ?? throw new ArgumentException("实际扬声器已不可用，请刷新音频设备。");
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(selected.Id);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
        if (level > 0) device.AudioEndpointVolume.Mute = false;
    }
}
