using System.IO;
using Pidge.Data;

namespace Pidge.Tests;

public class MigrationTests
{
    [Fact]
    public void Old_QuickMail_data_folder_moves_to_Pidge_and_database_is_renamed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pidge-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        var legacy = Path.Combine(root, "QuickMail");
        var target = Path.Combine(root, "Pidge");
        Directory.CreateDirectory(Path.Combine(legacy, "cache"));
        File.WriteAllText(Path.Combine(legacy, "quickmail.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        var used = Db.MigrateLegacyFolder(legacy, target);

        Assert.Equal(target, used);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("db", File.ReadAllText(Path.Combine(target, "pidge.db")));
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Existing_Pidge_folder_is_left_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "pidge-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "QuickMail"));
        Directory.CreateDirectory(Path.Combine(root, "Pidge"));

        Assert.Equal(Path.Combine(root, "Pidge"), Db.MigrateLegacyFolder(Path.Combine(root, "QuickMail"), Path.Combine(root, "Pidge")));
        Assert.True(Directory.Exists(Path.Combine(root, "QuickMail")));
        Directory.Delete(root, true);
    }
}
