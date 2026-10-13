using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Pidge.Services;

public record ReleaseInfo(Version Version, string Tag, string Notes, string MsiUrl, long MsiSize, string PageUrl);

/// <summary>Checks GitHub Releases for a newer MSI and installs it in place.</summary>
public static class UpdateService
{
    public const string Repository = "jakemorgangit/Pidge";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Pidge/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            return new ReleaseInfo(
                new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
                tag,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(),
                root.GetProperty("html_url").GetString() ?? "");
        }
        return null;
    }

    public static async Task CheckOnStartupAsync(Window owner)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            var latest = await GetLatestAsync();
            if (latest == null || latest.Version <= CurrentVersion) return;
            if (AppSettings.Current.SkippedVersion == latest.Tag) return;
            owner.Dispatcher.Invoke(() => new Views.UpdateWindow(latest) { Owner = owner }.ShowDialog());
        }
        catch
        {
            // Offline or rate-limited: try again next launch.
        }
    }

    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PidgeUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Pidge-{release.Version}.msi");

        using var resp = await Http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? release.MsiSize;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress.Report((double)read / total);
        }
        return path;
    }

    /// <summary>
    /// Runs the MSI (a major upgrade of the existing install) once Pidge has exited.
    /// The installer relaunches Pidge when it finishes.
    /// </summary>
    public static void InstallAndRestart(string msiPath)
    {
        var script = $"/c timeout /t 2 /nobreak >nul & msiexec /i \"{msiPath}\" /passive /norestart";
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false });
        Application.Current.Shutdown();
    }
}
