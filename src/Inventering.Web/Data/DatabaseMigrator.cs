using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Data;

/// <summary>
/// Uppdaterar databasen till senaste schema vid start.
/// - Tar en säkerhetskopia (VACUUM INTO) innan väntande migreringar körs.
/// - En databas från före migreringarna (skapad med EnsureCreated, utan historiktabell)
///   flyttas undan och en ny skapas – den finns bara i utvecklings-/testmiljöer.
/// </summary>
public static class DatabaseMigrator
{
    private const int KeepBackups = 10;

    public static void Run(AppDb db, string dataDir, ILogger log)
    {
        var file = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var exists = File.Exists(file) && new FileInfo(file).Length > 0;

        if (exists && IsLegacy(file))
        {
            db.Database.CloseConnection();
            SqliteConnection.ClearAllPools();
            var moved = $"{file}.fore-migreringar-{DateTime.Now:yyyyMMdd-HHmmss}";
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(file + suffix)) File.Move(file + suffix, moved + suffix);
            log.LogWarning("Databasen saknade migreringshistorik (skapad av en tidig version). Den har flyttats till {File} och en ny databas skapas.", moved);
            exists = false;
        }

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0) { EnableWal(db); return; }

        if (exists)
        {
            var dir = Path.Combine(dataDir, "backups");
            Directory.CreateDirectory(dir);
            var backup = Path.Combine(dir, $"inventory-{DateTime.Now:yyyyMMdd-HHmmss}-fore-{pending[^1]}.db");
            // VACUUM INTO ger en konsekvent kopia även när WAL används.
            db.Database.ExecuteSqlRaw("VACUUM INTO {0}", backup);
            log.LogInformation("Säkerhetskopia före uppdatering: {File}", backup);
            foreach (var old in new DirectoryInfo(dir).GetFiles("inventory-*.db").OrderByDescending(f => f.Name).Skip(KeepBackups))
                old.Delete();
        }

        log.LogInformation("Uppdaterar databasen: {Migrations}", string.Join(", ", pending));
        db.Database.Migrate();
        EnableWal(db);
    }

    private static bool IsLegacy(string file)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));
        return tables.Count > 0 && !tables.Contains("__EFMigrationsHistory");
    }

    private static void EnableWal(AppDb db) => db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}
