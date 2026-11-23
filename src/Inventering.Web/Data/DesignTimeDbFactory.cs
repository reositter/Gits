using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Inventering.Web.Data;

/// <summary>
/// Används bara av verktyget dotnet-ef när migreringar skapas, så att programmets
/// uppstart (Windows-tjänst, datakatalog, första admin) inte körs.
/// </summary>
public class DesignTimeDbFactory : IDesignTimeDbContextFactory<AppDb>
{
    public AppDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=design-time.db").Options);
}
