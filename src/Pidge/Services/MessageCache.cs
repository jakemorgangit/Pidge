using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MimeKit;
using Pidge.Data;
using Pidge.Models;

namespace Pidge.Services;

public record StorageReport(long DatabaseBytes, long CacheBytes, int CachedMessages, long UncompressedFiles)
{
    public long TotalBytes => DatabaseBytes + CacheBytes;
}

/// <summary>
/// Downloaded messages are kept on disk Brotli-compressed (.emlz), typically a third to a fifth of their
/// original size. Old plain .eml files from earlier versions are still read and converted when compacting.
/// </summary>
public static class MessageCache
{
    private const string Ext = ".emlz";

    public static string PathFor(MessageRecord m)
    {
        var folder = m.Folder == FolderKind.Custom ? "Custom_" + ShortHash(m.FolderPath) : m.Folder.ToString();
        return Path.Combine(Db.CacheDir, m.AccountId.ToString(), folder, $"{m.UidValidity}_{m.Uid}{Ext}");
    }

    private static string LegacyPath(MessageRecord m) => Path.ChangeExtension(PathFor(m), ".eml");

    private static string ShortHash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    public static bool IsCached(MessageRecord m) => File.Exists(PathFor(m)) || File.Exists(LegacyPath(m));

    public static async Task<MimeMessage?> TryLoadAsync(MessageRecord m)
    {
        var path = PathFor(m);
        try
        {
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // "last opened", used by the retention clean-up
                await using var fs = File.OpenRead(path);
                await using var br = new BrotliStream(fs, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                await br.CopyToAsync(ms);
                ms.Position = 0;
                return await MimeMessage.LoadAsync(ms);
            }
            var legacy = LegacyPath(m);
            if (File.Exists(legacy)) return await MimeMessage.LoadAsync(legacy);
        }
        catch
        {
            Delete(m); // corrupt cache entry: fetch again
        }
        return null;
    }

    public static async Task SaveAsync(MessageRecord m, MimeMessage message)
    {
        var path = PathFor(m);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var fs = File.Create(temp))
        await using (var br = new BrotliStream(fs, CompressionLevel.Optimal))
            await message.WriteToAsync(br);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(MessageRecord m)
    {
        try { File.Delete(PathFor(m)); } catch { /* ignore */ }
        try { File.Delete(LegacyPath(m)); } catch { /* ignore */ }
    }

    public static StorageReport Measure()
    {
        long db = new[] { "pidge.db", "pidge.db-wal" }
            .Select(f => new FileInfo(Path.Combine(Db.DataDir, f))).Where(f => f.Exists).Sum(f => f.Length);
        long cache = 0, legacy = 0;
        int count = 0;
        if (Directory.Exists(Db.CacheDir))
        {
            foreach (var f in new DirectoryInfo(Db.CacheDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cache += f.Length;
                count++;
                if (f.Extension == ".eml") legacy++;
            }
        }
        return new StorageReport(db, cache, count, legacy);
    }

    /// <summary>
    /// Compresses old uncompressed files, removes bodies nobody has opened for <paramref name="retentionDays"/> days
    /// (0 = keep forever; they re-download when opened), removes files for messages no longer in the cache, then compacts the database.
    /// </summary>
    public static async Task<(long before, long after)> CompactAsync(int retentionDays, CancellationToken ct = default)
    {
        var before = Measure().TotalBytes;
        if (Directory.Exists(Db.CacheDir))
        {
            List<MessageRecord> all;
            using (var c = Db.Open())
                all = c.Query<MessageRecord>("SELECT Id, AccountId, Folder, FolderPath, Uid, UidValidity FROM Messages").ToList();
            var wanted = all.ToDictionary(PathFor, m => m, StringComparer.OrdinalIgnoreCase);
            var cutoff = retentionDays > 0 ? DateTime.UtcNow.AddDays(-retentionDays) : DateTime.MinValue;

            foreach (var file in new DirectoryInfo(Db.CacheDir).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var asCompressed = file.Extension == ".eml" ? Path.ChangeExtension(file.FullName, Ext) : file.FullName;
                    if (file.Extension == ".tmp" || !wanted.TryGetValue(asCompressed, out var record) || file.LastWriteTimeUtc < cutoff)
                    {
                        file.Delete();
                        continue;
                    }
                    if (file.Extension == ".eml")
                    {
                        var message = await MimeMessage.LoadAsync(file.FullName, ct);
                        await SaveAsync(record, message);
                        File.SetLastWriteTimeUtc(PathFor(record), file.LastWriteTimeUtc);
                        file.Delete();
                    }
                }
                catch { /* skip files in use */ }
            }

            foreach (var dir in new DirectoryInfo(Db.CacheDir).EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
                if (!dir.EnumerateFileSystemInfos().Any()) try { dir.Delete(); } catch { /* ignore */ }
        }

        MailStore.Vacuum();
        AppSettings.Current.LastCompactUtc = DateTime.UtcNow;
        AppSettings.Save();
        return (before, Measure().TotalBytes);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
    };
}
