using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>Bolag, behörighet per användare och Visma-gateway per bolag.</summary>
public class CompanyService(IServiceScopeFactory scopes, IVismaGatewayFactory factory, IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("company-secrets");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(Company c)
    {
        if (string.IsNullOrEmpty(c.VismaPasswordProtected)) return null;
        try { return _protector.Unprotect(c.VismaPasswordProtected); } catch { return null; }
    }

    public async Task<List<Company>> ActiveAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.Companies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<IVismaGateway> GatewayAsync(int companyId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == companyId)
                ?? throw new KeyNotFoundException($"Bolag {companyId} finns inte.");
        return factory.For(c);
    }

    /// <summary>Bolag som användaren får jobba i. Admin: alla aktiva. Övriga: tilldelade, eller alla om inga tilldelats.</summary>
    public static async Task<List<Company>> ForUserAsync(AppDb db, User user)
    {
        var active = db.Companies.AsNoTracking().Where(c => c.IsActive);
        if (user.Role != UserRole.Admin)
        {
            var allowed = await db.UserCompanies.Where(x => x.UserId == user.Id).Select(x => x.CompanyId).ToListAsync();
            if (allowed.Count > 0) active = active.Where(c => allowed.Contains(c.Id));
        }
        return await active.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }
}
