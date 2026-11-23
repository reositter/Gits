using Inventering.Core.Domain;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>Håller streckkodsregler och extra streckkoder i minnet; laddas om när admin ändrar.</summary>
public class BarcodeService(IServiceScopeFactory scopes, ArticleCache articles)
{
    private volatile IReadOnlyList<BarcodeRule> _rules = [];
    private volatile IReadOnlyDictionary<string, BarcodeAlias> _aliases = new Dictionary<string, BarcodeAlias>();

    public async Task ReloadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        _rules = await db.BarcodeRules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
        _aliases = (await db.BarcodeAliases.AsNoTracking().ToListAsync()).ToDictionary(a => a.Code, StringComparer.Ordinal);
    }

    public ScanResult? Parse(string code) => Parse(code, _rules);

    /// <summary>Tolka med en given regeluppsättning (används för att testa en regel innan den sparas).</summary>
    public ScanResult? Parse(string code, IReadOnlyList<BarcodeRule> rules) =>
        BarcodeParser.Parse(code, _aliases, rules, articles.FindByEan, articles.Get);
}
