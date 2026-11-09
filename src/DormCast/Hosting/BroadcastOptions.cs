using System.Text.Json;

namespace DormCast.Hosting;

public sealed record BroadcastOptions(string Name = "宿舍之音", int Port = 8080, int FrameMs = 20, int BufferMs = 60,
    bool SyncEnabled = true, bool HostPlayback = false, string? CaptureDeviceId = null, string? OutputDeviceId = null, int HostHardwareCorrectionMs = 0)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60) throw new ArgumentException("广播名称须为 1–60 个字符。");
        if (Port is < 1024 or > 65535) throw new ArgumentException("端口须在 1024–65535 之间。");
        if (FrameMs is not (10 or 20)) throw new ArgumentException("Opus 帧长须为 10 或 20ms。");
        if (BufferMs is < 20 or > 200) throw new ArgumentException("接收缓冲目标须在 20–200ms 之间。");
        if (HostHardwareCorrectionMs is < 0 or > 1000) throw new ArgumentException("主机硬件补偿须在 0–1000ms 之间。");
        if (HostPlayback && !SyncEnabled) throw new ArgumentException("主机延迟播放需要启用统一同步。");
        if (HostPlayback && (string.IsNullOrWhiteSpace(CaptureDeviceId) || string.IsNullOrWhiteSpace(OutputDeviceId) || CaptureDeviceId == OutputDeviceId))
            throw new ArgumentException("主机同步播放需要不同的虚拟采集设备与实际扬声器。");
    }

    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DormCast", "settings.json");
    public static bool HasSavedSettings => File.Exists(SettingsPath);
    public static BroadcastOptions Load()
    {
        try { var result = JsonSerializer.Deserialize<BroadcastOptions>(File.ReadAllText(SettingsPath)) ?? new(); result.Validate(); return result; }
        catch { return new(); }
    }
    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, SettingsPath, true);
    }
}
