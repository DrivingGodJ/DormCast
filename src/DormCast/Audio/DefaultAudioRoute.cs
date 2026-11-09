using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace DormCast.Audio;

// IPolicyConfig is the Windows desktop endpoint policy ABI also used by EarTrumpet.
public sealed class DefaultAudioRoute : IDisposable
{
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicy
    {
        void Unused1(); void Unused2(); void Unused3(); void Unused4(); void Unused5(); void Unused6();
        void Unused7(); void Unused8(); void Unused9(); void Unused10();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }
    sealed record Recovery(int OwnerPid, long OwnerStartTicks, string VirtualId, Dictionary<Role, string> Originals);
    readonly Recovery _recovery;
    bool _disposed;
    static string RecoveryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DormCast", "audio-route-recovery.json");
    DefaultAudioRoute(Recovery recovery) => _recovery = recovery;
    static string GetDefault(Role role)
    { using var e = new MMDeviceEnumerator(); using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, role); return d.ID; }
    static void SetDefault(string id, Role role)
    {
        var client = (IPolicy)new PolicyClient();
        try { client.SetDefaultEndpoint(id, role); }
        finally { Marshal.ReleaseComObject(client); }
    }
    public static DefaultAudioRoute SwitchTo(string virtualId, string physicalOutputId)
    {
        RecoverIfNeeded();
        if (File.Exists(RecoveryPath)) throw new InvalidOperationException("已有主机同步广播正在使用默认音频路由，请先停止它。");
        var originals = new Dictionary<Role, string> { [Role.Console] = GetDefault(Role.Console), [Role.Multimedia] = GetDefault(Role.Multimedia) };
        // Driver setup may already have selected the silent cable as default. Restore the chosen
        // real speaker in that case, so stopping the monitor cannot leave Windows inaudible.
        foreach (var role in originals.Keys.ToArray()) if (originals[role] == virtualId) originals[role] = physicalOutputId;
        using var process = Process.GetCurrentProcess();
        var recovery = new Recovery(process.Id, process.StartTime.ToUniversalTime().Ticks, virtualId, originals);
        Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath)!);
        using (var file = new FileStream(RecoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) JsonSerializer.Serialize(file, recovery);
        var route = new DefaultAudioRoute(recovery);
        try
        {
            foreach (var role in originals.Keys) SetDefault(virtualId, role);
            if (GetDefault(Role.Multimedia) != virtualId) throw new InvalidOperationException("Windows 未成功切换到虚拟音频设备。");
            return route;
        }
        catch { route.Dispose(); throw; }
    }
    public static void RecoverIfNeeded()
    {
        if (!File.Exists(RecoveryPath)) return;
        var recovery = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(RecoveryPath)) ?? throw new IOException("音频路由恢复文件无效。");
        try
        {
            using var owner = Process.GetProcessById(recovery.OwnerPid);
            if (owner.StartTime.ToUniversalTime().Ticks == recovery.OwnerStartTicks && !owner.HasExited) return;
        }
        catch (ArgumentException) { }
        Restore(recovery);
    }
    static void Restore(Recovery recovery)
    {
        var available = AudioDevices.RenderDevices().Select(d => d.Id).ToHashSet();
        foreach (var (role, original) in recovery.Originals)
        {
            // Preserve a default device the user deliberately changed during broadcasting.
            if (available.Contains(original) && GetDefault(role) == recovery.VirtualId) SetDefault(original, role);
        }
        File.Delete(RecoveryPath);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Restore(_recovery); }
}
