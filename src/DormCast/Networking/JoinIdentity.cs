using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DormCast.Networking;

public sealed record JoinIdentity(string HostName, string Token, string? MachineFingerprint = null)
{
    const string BackupKey = @"Software\DrivingGodJ\DormCast\Identity";
    static readonly Lazy<JoinIdentity> Current = new(Load);
    public static JoinIdentity Persistent => Current.Value;
    public static bool IsValid(JoinIdentity? value) => value != null && value.HostName != null && value.Token != null && Regex.IsMatch(value.HostName, "^dormcast-[a-f0-9]{8,24}\\.local$") && Regex.IsMatch(value.Token, "^[a-f0-9]{64}$");
    public static JoinIdentity Resolve(JoinIdentity? file, JoinIdentity? backup, string machineKey)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("DormCast machine identity v1:" + machineKey))).ToLowerInvariant();
        bool Own(JoinIdentity? value) => IsValid(value) && (value!.MachineFingerprint == null || value.MachineFingerprint == fingerprint);
        // A registry backup survives a cleaned application-data directory. Legacy links stay valid.
        var recovered = Own(backup) ? backup : Own(file) ? file : null;
        return recovered is not null ? recovered with { MachineFingerprint = fingerprint } : new JoinIdentity($"dormcast-{fingerprint[..16]}.local", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), fingerprint);
    }
    static JoinIdentity? Parse(string? text) { try { return text == null ? null : JsonSerializer.Deserialize<JoinIdentity>(text); } catch (JsonException) { return null; } }
    static JoinIdentity Load()
    {
        using var mutex = new Mutex(false, @"Local\DormCast.JoinIdentity");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DormCast");
            var path = Path.Combine(directory, "join-identity.json"); Directory.CreateDirectory(directory);
            JoinIdentity? file = null;
            try { if (File.Exists(path)) file = Parse(File.ReadAllText(path)); } catch (IOException) { }
            using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
            using var system = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineKey = system?.GetValue("MachineGuid") as string ?? Environment.MachineName;
            var value = Resolve(file, Parse(backup.GetValue("JoinIdentity") as string), machineKey);
            var serialized = JsonSerializer.Serialize(value);
            backup.SetValue("JoinIdentity", serialized, RegistryValueKind.String); backup.Flush();
            var temporary = path + ".tmp"; File.WriteAllText(temporary, serialized); File.Move(temporary, path, true);
            return value;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
