using System.Collections.Concurrent;
using Inventering.Core.Domain;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>
/// Hela artikelregistret per bolag hålls i minnet (och i SQLite för snabb uppstart).
/// Skanning och sökning går aldrig mot Visma – ADK används bara för periodisk uppdatering i bakgrunden.
/// </summary>
public class ArticleCache(IServiceScopeFactory scopes, CompanyService companies, ILogger<ArticleCache> log)
{
    private readonly ConcurrentDictionary<int, Snapshot> _snaps = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<int, (DateTimeOffset? At, string? Error)> _state = new();

    private Snapshot Snap(int companyId) => _snaps.TryGetValue(companyId, out var s) ? s : Snapshot.Empty;

    public DateTimeOffset? LastRefresh(int companyId) => _state.TryGetValue(companyId, out var s) ? s.At : null;
    public string? LastError(int companyId) => _state.TryGetValue(companyId, out var s) ? s.Error : null;
    public int Count(int companyId) => Snap(companyId).All.Count;

    public Article? Get(int companyId, string articleNo) => Snap(companyId).ByArticleNo.GetValueOrDefault(articleNo.Trim());
    public Article? FindByEan(int companyId, string ean) => Snap(companyId).ByEan.GetValueOrDefault(ean);
    public IReadOnlyDictionary<string, Article> ByArticleNo(int companyId) => Snap(companyId).ByArticleNo;
    public IReadOnlyList<Article> All(int companyId) => Snap(companyId).All;

    /// <summary>Sökning på artikelnr, benämning och EAN mot ett förberett index i minnet.</summary>
    public IReadOnlyList<Article> Search(int companyId, string q, int max = 25)
    {
        q = q.Trim();
        if (q.Length == 0) return [];
        var snap = Snap(companyId);
        var terms = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<(Article A, int Rank)>();
        for (var i = 0; i < snap.All.Count; i++)
        {
            var text = snap.SearchText[i];
            if (!terms.All(text.Contains)) continue;
            var a = snap.All[i];
            var rank = a.ArticleNo.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                : a.ArticleNo.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            hits.Add((a, rank));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.A.ArticleNo, StringComparer.OrdinalIgnoreCase).Take(max).Select(h => h.A).ToList();
    }

    /// <summary>Läser in alla bolags artiklar från SQLite vid uppstart – skanning fungerar även om Visma inte svarar.</summary>
    public async Task LoadFromDbAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var all = await db.Articles.AsNoTracking().ToListAsync(ct);
        foreach (var g in all.GroupBy(a => a.CompanyId))
        {
            _snaps[g.Key] = new Snapshot(g.ToList());
            _state[g.Key] = (g.Max(a => a.UpdatedAt), null);
        }
    }

    /// <summary>Hämtar alla artiklar för ett bolag från Visma och ersätter cachen.</summary>
    public async Task RefreshAsync(int companyId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(companyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var visma = await companies.GatewayAsync(companyId);
            var articles = (await visma.GetArticlesAsync(ct))
                .DistinctBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase).ToList();
            var now = DateTimeOffset.UtcNow;
            foreach (var a in articles)
            {
                a.CompanyId = companyId;
                a.UpdatedAt = now;
                a.Ean = string.IsNullOrWhiteSpace(a.Ean) ? null : CodeLookup.NormalizeEan(a.Ean);
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Articles.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync(ct);
            db.Articles.AddRange(articles);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _snaps[companyId] = new Snapshot(articles);
            _state[companyId] = (now, null);
            log.LogInformation("Artikelcache bolag {Company}: {Count} artiklar", companyId, articles.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state[companyId] = (LastRefresh(companyId), ex.Message);
            log.LogError(ex, "Kunde inte uppdatera artikelcache för bolag {Company}", companyId);
            throw;
        }
        finally { gate.Release(); }
    }

    public void Forget(int companyId) => _snaps.TryRemove(companyId, out _);

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new([]);

        public Snapshot(List<Article> all)
        {
            All = all;
            ByArticleNo = all.DistinctBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase);
            ByEan = all.Where(a => a.Ean != null).DistinctBy(a => a.Ean!).ToDictionary(a => a.Ean!, StringComparer.Ordinal);
            // Förberäknad söktext (gemener) så att fritextsökning bara blir strängjämförelser i minnet.
            SearchText = all.Select(a => $"{a.ArticleNo} {a.Name} {a.Ean}".ToLowerInvariant()).ToArray();
        }
        public List<Article> All { get; }
        public Dictionary<string, Article> ByArticleNo { get; }
        public Dictionary<string, Article> ByEan { get; }
        public string[] SearchText { get; }
    }
}

public class ArticleRefreshService(ArticleCache cache, CompanyService companies, IConfiguration config, ILogger<ArticleRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await cache.LoadFromDbAsync(ct);
        var minutes = config.GetValue("Visma:ArticleRefreshMinutes", 15);
        while (!ct.IsCancellationRequested)
        {
            foreach (var c in await companies.ActiveAsync())
            {
                try { await cache.RefreshAsync(c.Id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { log.LogWarning("Artikeluppdatering misslyckades för {Company}: {Msg}", c.Name, ex.Message); }
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, minutes)), ct);
        }
    }
}
