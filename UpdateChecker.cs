using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScanDesk;

public sealed record UpdateInfo(Version Version, string PageUrl, string DownloadUrl, string AssetName, long Size, string? Sha256);

/// <summary>
/// Checks GitHub releases for a newer ScanDesk, downloads the installer, verifies it and runs it.
/// </summary>
public static class UpdateChecker
{
    const string Repo = "Jack-Mutsers/ScanDesk";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ScanDesk-Updater");
        return http;
    }

    /// <summary>The running version as text, e.g. "1.0.3" (or "0.0.0-local" for a local build).</summary>
    public static string CurrentVersionText { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0]; // strip the "+commit" suffix the SDK appends

    /// <summary>The running version, or null for local development builds (which never auto-update).</summary>
    public static Version? CurrentVersion { get; } =
        Version.TryParse(CurrentVersionText, out var v) && v > new Version(0, 0, 0) ? v : null;

    /// <summary>Returns the latest release if it's newer than the running version, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        if (CurrentVersion != null && latest <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("ScanDesk-Setup-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                          digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..] : null;
            return new UpdateInfo(latest,
                root.GetProperty("html_url").GetString() ?? ReleasesUrl,
                asset.GetProperty("browser_download_url").GetString()!,
                name, asset.GetProperty("size").GetInt64(), sha);
        }
        return null; // release without an installer attached
    }

    /// <summary>Downloads the installer to a temp folder and checks its size and SHA-256. Reports progress 0..1.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ScanDesk-Update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, update.AssetName);

        using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                progress?.Report(update.Size > 0 ? (double)done / update.Size : 0);
            }
        }

        var info = new FileInfo(path);
        if (update.Size > 0 && info.Length != update.Size)
        {
            info.Delete();
            throw new InvalidDataException("The download was incomplete. Please try again.");
        }
        if (update.Sha256 != null)
        {
            await using var fs = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer failed its safety check (checksum mismatch), so it was not run.");
            }
        }
        return path;
    }

    /// <summary>
    /// Starts the installer in silent mode; it closes ScanDesk, updates it and opens it again.
    /// Returns false if the user declined the Windows permission prompt.
    /// </summary>
    public static bool LaunchInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true, // needed for the administrator prompt
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
