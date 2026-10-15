using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using Pidge.Data;

namespace Pidge.Services;

/// <summary>A company's logo (cached PNG) and principal brand colour.</summary>
public record BrandInfo(string Domain, string? IconPath, Color? Color);

/// <summary>
/// Logos and brand colours for organisations that email you. Looked up once per domain (from the company's own
/// website, falling back to DuckDuckGo's icon service), cached on disk and refreshed monthly. Never fetched when
/// you open an email, so it can't be used to tell a sender you've read their mail.
/// </summary>
public static partial class BrandService
{
    private static readonly ConcurrentDictionary<string, BrandInfo?> Memory = new();
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    private static readonly SemaphoreSlim Throttle = new(4, 4);
    private static readonly TimeSpan RefreshFound = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryMissing = TimeSpan.FromDays(7);
    private const int IconSize = 64;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Raised (on a background thread) when a domain's logo/colour has been fetched.</summary>
    public static event Action<string>? BrandReady;

    public static bool Enabled => AppSettings.Current.ShowBrandLogos || AppSettings.Current.BrandTint;

    private static string Folder => Path.Combine(Db.DataDir, "brands");

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Pidge");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/*;q=0.9,*/*;q=0.8");
        return c;
    }

    /// <summary>
    /// The brand for a sender if it's already known. Unknown organisations are fetched in the background and
    /// <see cref="BrandReady"/> fires when done. Personal mailboxes (Gmail, Outlook…) always return null.
    /// </summary>
    public static BrandInfo? Lookup(string address)
    {
        if (!Enabled || !Domains.IsOrganisation(address)) return null;
        var domain = Domains.BaseDomain(Domains.DomainOf(address));
        if (Memory.TryGetValue(domain, out var known)) return known;

        var row = Load(domain);
        if (row != null)
        {
            var info = ToInfo(row);
            Memory[domain] = info;
            var age = DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(row.FetchedUnix).UtcDateTime;
            if (age < (info?.IconPath != null || info?.Color != null ? RefreshFound : RetryMissing)) return info;
            Queue(domain);
            return info;
        }

        Queue(domain);
        return null;
    }

    public static string DomainFor(string address) => Domains.BaseDomain(Domains.DomainOf(address));

    private static void Queue(string domain)
    {
        if (!InFlight.TryAdd(domain, 0)) return;
        _ = Task.Run(async () =>
        {
            await Throttle.WaitAsync();
            try
            {
                var info = await FetchAsync(domain, CancellationToken.None);
                Save(info);
                Memory[domain] = info.IconPath == null && info.Color == null ? null : info;
                BrandReady?.Invoke(domain);
            }
            catch (Exception ex)
            {
                Log.Error($"Brand lookup failed for {domain}", ex);
            }
            finally
            {
                Throttle.Release();
                InFlight.TryRemove(domain, out _);
            }
        });
    }

    /// <summary>Finds the best logo and the brand colour for a domain. Doesn't throw for network problems.</summary>
    internal static async Task<BrandInfo> FetchAsync(string domain, CancellationToken ct)
    {
        var candidates = new List<(Uri url, int score)>();
        string? themeColor = null;

        foreach (var home in new[] { $"https://{domain}/", $"https://www.{domain}/" })
        {
            try
            {
                using var resp = await Http.GetAsync(home, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var baseUri = resp.RequestMessage?.RequestUri ?? new Uri(home);
                var html = await ReadLimitedAsync(resp, 400_000, ct);
                foreach (Match link in LinkTag().Matches(html))
                {
                    var tag = link.Value;
                    var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
                    var href = Attr(tag, "href");
                    if (href == null || !rel.Contains("icon") || rel.Contains("mask")) continue;
                    if (href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || (Attr(tag, "type")?.Contains("svg") ?? false)) continue;
                    if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href), out var url)) continue;
                    var size = Attr(tag, "sizes") is { } s && SizeRegex().Match(s) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 32;
                    candidates.Add((url, (rel.Contains("apple-touch") ? 1000 : 0) + Math.Min(size, 512)));
                }
                themeColor ??= MetaThemeColor().Match(html) is { Success: true } tc ? tc.Groups["c"].Value : null;
                candidates.Add((new Uri(baseUri, "/favicon.ico"), 20));
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
        }
        candidates.Add((new Uri($"https://{domain}/favicon.ico"), 10));
        candidates.Add((new Uri($"https://icons.duckduckgo.com/ip3/{domain}.ico"), 1));

        string? iconPath = null;
        BitmapSource? icon = null;
        foreach (var (url, _) in candidates.OrderByDescending(c => c.score).DistinctBy(c => c.url))
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 64 || bytes.Length > 2_000_000) continue;
                icon = Decode(bytes);
                if (icon == null) continue;
                iconPath = SaveIcon(domain, icon);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException
                                           or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }

        Color? color = ParseColor(themeColor) is { } c && IsBrandColour(c) ? c : icon != null ? DominantColour(icon) : null;
        return new BrandInfo(domain, iconPath, color);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[max];
        int total = 0, n;
        while (total < max && (n = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct)) > 0) total += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string? Attr(string tag, string name) =>
        Regex.Match(tag, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups["v"].Value.Trim() : null;

    /// <summary>Decodes PNG/ICO/JPEG/GIF/BMP, picking the largest frame of an icon and scaling it to 64×64.</summary>
    internal static BitmapSource? Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).FirstOrDefault();
        if (frame == null || frame.PixelWidth < 16 || frame.PixelHeight < 16) return null;

        BitmapSource source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var scale = (double)IconSize / Math.Max(source.PixelWidth, source.PixelHeight);
        if (Math.Abs(scale - 1) > 0.01) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var result = new WriteableBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0));
        result.Freeze();
        return result;
    }

    private static string SaveIcon(string domain, BitmapSource icon)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, domain + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(icon));
        using (var fs = File.Create(path)) encoder.Save(fs);
        return path;
    }

    // ---------------- colour ----------------
    internal static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch { return null; }
    }

    /// <summary>A usable brand colour: not white, black or grey.</summary>
    internal static bool IsBrandColour(Color c)
    {
        var (_, s, v) = ToHsv(c);
        return s >= 0.25 && v >= 0.25; // white and greys have little saturation, black is dark
    }

    /// <summary>The most common strong colour in an image (ignoring transparent, white, black and grey pixels).</summary>
    internal static Color? DominantColour(BitmapSource image)
    {
        var bmp = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        bmp.CopyPixels(px, stride, 0);

        var bins = new (double weight, double r, double g, double b)[24];
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 200) continue;
            var c = Color.FromRgb(px[i + 2], px[i + 1], px[i]);
            if (!IsBrandColour(c)) continue;
            var (hue, s, _) = ToHsv(c);
            var bin = (int)(hue / 15) % 24;
            bins[bin].weight += s;
            bins[bin].r += c.R * s;
            bins[bin].g += c.G * s;
            bins[bin].b += c.B * s;
        }
        var best = bins.OrderByDescending(b => b.weight).First();
        if (best.weight < 8) return null; // too few coloured pixels: a monochrome logo
        return Color.FromRgb((byte)Math.Round(best.r / best.weight), (byte)Math.Round(best.g / best.weight), (byte)Math.Round(best.b / best.weight));
    }

    private static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    // ---------------- storage ----------------
    private class BrandRow
    {
        public string Domain { get; set; } = "";
        public string IconFile { get; set; } = "";
        public string Color { get; set; } = "";
        public long FetchedUnix { get; set; }
    }

    private static BrandRow? Load(string domain)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<BrandRow>("SELECT * FROM Brands WHERE Domain=@domain", new { domain });
    }

    private static BrandInfo? ToInfo(BrandRow row)
    {
        var icon = row.IconFile.Length > 0 && File.Exists(row.IconFile) ? row.IconFile : null;
        var color = ParseColor(row.Color);
        return icon == null && color == null ? null : new BrandInfo(row.Domain, icon, color);
    }

    private static void Save(BrandInfo info)
    {
        using var c = Db.Open();
        c.Execute("""
            INSERT INTO Brands(Domain,IconFile,Color,FetchedUnix) VALUES(@d,@i,@c,@n)
            ON CONFLICT(Domain) DO UPDATE SET IconFile=@i, Color=@c, FetchedUnix=@n
            """, new { d = info.Domain, i = info.IconPath ?? "", c = info.Color?.ToString() ?? "", n = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"(\d+)x\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']theme-color[""'][^>]*content\s*=\s*[""'](?<c>[^""']+)[""']|<meta[^>]+content\s*=\s*[""'](?<c>[^""']+)[""'][^>]*name\s*=\s*[""']theme-color[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaThemeColor();
}
