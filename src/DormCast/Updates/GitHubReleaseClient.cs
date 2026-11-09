using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DormCast.Updates;

public sealed record ReleaseUpdate(Version Version, string Notes, Uri Download, long Size, string Sha256);

public sealed class GitHubReleaseClient : IDisposable
{
    public const string Repository = "DrivingGodJ/DormCast";
    public const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";
    const long MaximumSize = 300 * 1024 * 1024;
    readonly HttpClient _http;
    readonly bool _ownsClient;
    public GitHubReleaseClient(HttpClient? http = null)
    { _ownsClient = http == null; _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; }
    public async Task<ReleaseUpdate?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/latest");
        request.Headers.UserAgent.ParseAdd("DormCast/" + current); request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        { if (output.Length + read > 1024 * 1024) throw new InvalidDataException("更新信息过大。"); output.Write(buffer, 0, read); }
        return Parse(output.ToArray(), current);
    }
    public static ReleaseUpdate? Parse(ReadOnlyMemory<byte> json, Version current)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+$") || !Version.TryParse(tag[1..], out var version) || version <= current) return null;
        var name = $"DormCast-{version}-Setup-x64.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (asset.GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("更新包地址不属于官方仓库。");
            var size = asset.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumSize) throw new InvalidDataException("更新包大小无效。");
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("该 Release 缺少 SHA-256 校验信息，请从官方 Release 页面手动下载。");
            return new ReleaseUpdate(version, root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "", new Uri(expected), size, digest[7..].ToLowerInvariant());
        }
        throw new InvalidDataException("新版本尚未提供 Windows x64 安装包，请稍后再试。");
    }
    public async Task<string> DownloadAsync(ReleaseUpdate update, string directory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Revalidate even when a caller constructed the release object itself.
        var expected = $"https://github.com/{Repository}/releases/download/v{update.Version}/DormCast-{update.Version}-Setup-x64.exe";
        if (update.Download.AbsoluteUri != expected || update.Size is <= 0 or > MaximumSize || !Regex.IsMatch(update.Sha256, "^[a-f0-9]{64}$")) throw new InvalidDataException("更新信息无效。");
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, $"DormCast-{update.Version}-{Guid.NewGuid():N}.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.Download); request.Headers.UserAgent.ParseAdd("DormCast-Updater/1.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri;
            if (final == null || final.Scheme != "https" || !(final.Host == "github.com" || final.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新下载重定向无效。");
            if (response.Content.Headers.ContentLength is { } length && length != update.Size) throw new InvalidDataException("更新包长度不符。");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                { total += read; if (total > update.Size) throw new InvalidDataException("更新包超过声明大小。"); hash.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false); progress?.Report(total / (double)update.Size); }
                if (total != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256))) throw new InvalidDataException("更新包 SHA-256 校验失败，已取消安装。");
            }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
