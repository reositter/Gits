using Inventering.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<UserCompany> UserCompanies => Set<UserCompany>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<InventorySession> Sessions => Set<InventorySession>();
    public DbSet<StockSnapshot> Snapshots => Set<StockSnapshot>();
    public DbSet<CountLine> Lines => Set<CountLine>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<BarcodeRule> BarcodeRules => Set<BarcodeRule>();
    public DbSet<BarcodeAlias> BarcodeAliases => Set<BarcodeAlias>();
    public DbSet<SendResult> SendResults => Set<SendResult>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.UserName).IsUnique();
            e.Property(x => x.UserName).UseCollation("NOCASE");
        });
        b.Entity<Company>(e => e.HasIndex(x => x.CompanyPath).IsUnique());
        b.Entity<UserCompany>(e => e.HasKey(x => new { x.UserId, x.CompanyId }));
        b.Entity<Article>(e =>
        {
            e.HasKey(x => new { x.CompanyId, x.ArticleNo });
            e.Property(x => x.ArticleNo).UseCollation("NOCASE");
            e.HasIndex(x => new { x.CompanyId, x.Ean });
        });
        b.Entity<InventorySession>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Status });
            e.HasMany(x => x.Snapshot).WithOne().HasForeignKey(x => x.SessionId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SessionId);
        });
        b.Entity<StockSnapshot>(e => e.HasKey(x => new { x.SessionId, x.ArticleNo }));
        b.Entity<CountLine>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.SessionId, x.ArticleNo });
        });
        b.Entity<SendResult>(e => e.HasIndex(x => x.SessionId));
        b.Entity<BarcodeAlias>(e => { e.HasKey(x => new { x.CompanyId, x.Code }); e.HasIndex(x => new { x.CompanyId, x.ArticleNo }); });
        b.Entity<Submission>(e => e.HasIndex(x => new { x.SessionId, x.UserId }));
        b.Entity<CountLine>().HasIndex(x => x.SubmissionId);

        // SQLite saknar decimal och DateTimeOffset-jämförelser → lagra som text/ticks.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (prop.ClrType == typeof(decimal) || prop.ClrType == typeof(decimal?))
                prop.SetColumnType("TEXT");
            if (prop.ClrType == typeof(DateTimeOffset))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            if (prop.ClrType == typeof(DateTimeOffset?))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                    v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
                    v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        }
    }
}
