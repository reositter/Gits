using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>
/// Håller artikelregistret i minnet (och i SQLite) så att skanning inte behöver vänta på ADK.
/// </summary>
public class ArticleCache(IServiceScopeFactory scopes, IVismaGateway visma, ILogger<ArticleCache> log)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile Snapshot _snap = new([]);

    public DateTimeOffset? LastRefresh { get; private set; }
    public string? LastError { get; private set; }
    public int Count => _snap.All.Count;

    public Article? Find(string code) => CodeLookup.Find(code, _snap.ByEan, _snap.ByArticleNo);

    public Article? Get(string articleNo) => _snap.ByArticleNo.GetValueOrDefault(articleNo);

    public IReadOnlyDictionary<string, Article> ByArticleNo => _snap.ByArticleNo;

    public IReadOnlyList<Article> All => _snap.All;

    public IReadOnlyList<Article> Search(string q, int max = 25)
    {
        q = q.Trim();
        if (q.Length == 0) return [];
        return _snap.All
            .Where(a => a.ArticleNo.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || (a.Ean?.Contains(q) ?? false))
            .OrderBy(a => a.ArticleNo.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase)
            .Take(max).ToList();
    }

    /// <summary>Läser in från SQLite vid uppstart så att skanning fungerar även om Visma inte svarar.</summary>
    public async Task LoadFromDbAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        _snap = new Snapshot(await db.Articles.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>Hämtar alla artiklar från Visma och uppdaterar cachen.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var articles = await visma.GetArticlesAsync(ct);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Articles.ExecuteDeleteAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var a in articles)
            {
                a.UpdatedAt = now;
                a.Ean = string.IsNullOrWhiteSpace(a.Ean) ? null : CodeLookup.NormalizeEan(a.Ean);
            }
            db.Articles.AddRange(articles.DistinctBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            _snap = new Snapshot(articles.ToList());
            LastRefresh = now;
            LastError = null;
            log.LogInformation("Artikelcache uppdaterad: {Count} artiklar", articles.Count);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.LogError(ex, "Kunde inte uppdatera artikelcache från Visma");
            throw;
        }
        finally { _refreshLock.Release(); }
    }

    private sealed class Snapshot
    {
        public Snapshot(List<Article> all)
        {
            All = all;
            ByArticleNo = all.DistinctBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase);
            // Om två artiklar har samma EAN vinner den första – varning loggas inte här men syns vid sökning.
            ByEan = all.Where(a => a.Ean != null).DistinctBy(a => a.Ean!)
                .ToDictionary(a => a.Ean!, StringComparer.Ordinal);
        }
        public List<Article> All { get; }
        public Dictionary<string, Article> ByArticleNo { get; }
        public Dictionary<string, Article> ByEan { get; }
    }
}

public class ArticleRefreshService(ArticleCache cache, IConfiguration config, ILogger<ArticleRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await cache.LoadFromDbAsync(ct);
        var minutes = config.GetValue("Visma:ArticleRefreshMinutes", 15);
        while (!ct.IsCancellationRequested)
        {
            try { await cache.RefreshAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Artikeluppdatering misslyckades: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, minutes)), ct);
        }
    }
}
