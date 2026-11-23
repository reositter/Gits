using Inventering.Core.Domain;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>Streckkodsregler och extra streckkoder i minnet; laddas om när admin ändrar.</summary>
public class BarcodeService(IServiceScopeFactory scopes, ArticleCache articles)
{
    private volatile IReadOnlyList<BarcodeRule> _rules = [];
    private volatile Dictionary<int, Dictionary<string, BarcodeAlias>> _aliasMaps = new();

    public async Task ReloadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        _rules = await db.BarcodeRules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
        var aliases = await db.BarcodeAliases.AsNoTracking().ToListAsync();
        _aliasMaps = aliases.GroupBy(a => a.CompanyId).ToDictionary(g => g.Key, g => g.ToDictionary(a => a.Code, StringComparer.Ordinal));
    }

    /// <summary>Regler som gäller bolaget: bolagets egna + de som gäller alla bolag.</summary>
    public IReadOnlyList<BarcodeRule> RulesFor(int companyId) =>
        _rules.Where(r => r.CompanyId is null || r.CompanyId == companyId).ToList();

    public ScanResult? Parse(int companyId, string code) => Parse(companyId, code, RulesFor(companyId));

    public ScanResult? Parse(int companyId, string code, IReadOnlyList<BarcodeRule> rules) =>
        BarcodeParser.Parse(code,
            _aliasMaps.TryGetValue(companyId, out var map) ? map : new Dictionary<string, BarcodeAlias>(),
            rules, ean => articles.FindByEan(companyId, ean), no => articles.Get(companyId, no));
}
