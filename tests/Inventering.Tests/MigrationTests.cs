using Inventering.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inventering.Tests;

public class MigrationTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "inv-mig-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("DataDirectory", _dataDir));

    [Fact]
    public void New_database_is_created_by_migrations()
    {
        using var f = Factory();
        f.Server.ToString();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.NotEmpty(db.Database.GetAppliedMigrations());
        Assert.True(db.Users.Any()); // första admin skapad
    }

    [Fact]
    public void Model_has_no_changes_missing_from_migrations()
    {
        // Fångar att någon ändrat entiteterna utan att skapa en ny migrering.
        using var db = new DesignTimeDbFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges(), "Modellen har ändringar som saknar migrering – kör: dotnet ef migrations add <Namn> --project src/Inventering.Web --output-dir Data/Migrations");
    }

    [Fact]
    public void Legacy_database_without_history_is_moved_aside()
    {
        Directory.CreateDirectory(_dataDir);
        var file = Path.Combine(_dataDir, "inventory.db");
        using (var conn = new SqliteConnection($"Data Source={file};Pooling=false"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE Users (Id INTEGER PRIMARY KEY, UserName TEXT)";
            cmd.ExecuteNonQuery();
        }
        using var f = Factory();
        f.Server.ToString();
        Assert.Single(Directory.GetFiles(_dataDir, "inventory.db.fore-migreringar-*").Where(p => !p.EndsWith("-wal") && !p.EndsWith("-shm")));
        using var scope = f.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<AppDb>().Database.GetPendingMigrations());
    }

    [Fact]
    public void Backup_with_vacuum_into_works()
    {
        using var f = Factory();
        f.Server.ToString();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var backup = Path.Combine(_dataDir, "test-backup.db");
        db.Database.ExecuteSqlRaw("VACUUM INTO {0}", backup);
        using var conn = new SqliteConnection($"Data Source={backup};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
