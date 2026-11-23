using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

public class InventoryService(AppDb db, ArticleCache cache, CompanyService companies, ILogger<InventoryService> log)
{
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    /// <summary>Skapar inventering för bolaget och sparar Visma-saldot för alla lagerartiklar just nu.</summary>
    public async Task<InventorySession> CreateAsync(int companyId, string name, int userId)
    {
        await cache.RefreshAsync(companyId); // Färskt saldo som utgångsläge – kastar om Visma inte svarar.
        var s = new InventorySession { CompanyId = companyId, Name = name.Trim(), CreatedByUserId = userId, Status = SessionStatus.Open };
        s.Snapshot = cache.All(companyId).Where(a => a.IsStockItem)
            .Select(a => new StockSnapshot { ArticleNo = a.ArticleNo, Quantity = a.StockQuantity }).ToList();
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    public async Task<(IReadOnlyList<ArticleSummary> Summary, IReadOnlyList<Article> Uncounted)> SummarizeAsync(InventorySession session)
    {
        // Bara inlämnade (signerade) rader räknas. Utkast syns för admin som varning.
        var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == session.Id && l.SubmissionId != null).ToListAsync();
        var snapshot = await db.Snapshots.AsNoTracking().Where(s => s.SessionId == session.Id)
            .ToDictionaryAsync(s => s.ArticleNo, s => s.Quantity, StringComparer.OrdinalIgnoreCase);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserName : u.DisplayName);
        var summary = InventoryAggregator.Summarize(lines, snapshot, cache.ByArticleNo(session.CompanyId), users);
        var uncounted = InventoryAggregator.Uncounted(lines, cache.All(session.CompanyId));
        return (summary, uncounted);
    }

    /// <summary>
    /// Skickar en låst inventering till bolagets Visma: ett värde (summan av alla inlämnade räkningar) per artikel.
    /// Artiklar som inte räknats rörs inte, om inte <paramref name="zeroUncounted"/> anges.
    /// </summary>
    public async Task<IReadOnlyList<SendResult>> SendAsync(InventorySession session, int userId, bool zeroUncounted)
    {
        if (!await SendLock.WaitAsync(0)) throw new InvalidOperationException("En överföring pågår redan.");
        try
        {
            await db.Entry(session).ReloadAsync();
            if (session.Status != SessionStatus.Locked)
                throw new InvalidOperationException("Inventeringen måste vara låst innan den skickas.");

            await cache.RefreshAsync(session.CompanyId); // Aktuellt saldo för loggen/varningar.
            var (summary, uncounted) = await SummarizeAsync(session);
            var snapshot = summary.ToDictionary(s => s.ArticleNo, s => s.SnapshotQuantity, StringComparer.OrdinalIgnoreCase);

            var rows = summary.Select(s => new InventoryWrite(s.ArticleNo, s.CountedQuantity)).ToList();
            if (zeroUncounted)
            {
                var snap = await db.Snapshots.Where(x => x.SessionId == session.Id)
                    .ToDictionaryAsync(x => x.ArticleNo, x => x.Quantity, StringComparer.OrdinalIgnoreCase);
                foreach (var a in uncounted)
                {
                    rows.Add(new InventoryWrite(a.ArticleNo, 0));
                    snapshot[a.ArticleNo] = snap.GetValueOrDefault(a.ArticleNo);
                }
            }

            var visma = await companies.GatewayAsync(session.CompanyId);
            var results = await visma.WriteInventoryAsync(rows, $"Inventering #{session.Id} {session.Name}");
            var byNo = rows.ToDictionary(r => r.ArticleNo, StringComparer.OrdinalIgnoreCase);
            var saved = results.Select(r => new SendResult
            {
                SessionId = session.Id, ArticleNo = r.ArticleNo,
                CountedQuantity = byNo.TryGetValue(r.ArticleNo, out var w) ? w.CountedQuantity : 0,
                SnapshotQuantity = snapshot.GetValueOrDefault(r.ArticleNo),
                StockBeforeWrite = r.StockBefore, Success = r.Success, Message = r.Message,
            }).ToList();
            db.SendResults.AddRange(saved);

            session.Status = SessionStatus.Sent;
            session.SentAt = DateTimeOffset.UtcNow;
            session.SentByUserId = userId;
            await db.SaveChangesAsync();
            log.LogInformation("Inventering {Id} (bolag {Company}) skickad: {Ok} ok, {Fail} fel", session.Id, session.CompanyId,
                saved.Count(r => r.Success), saved.Count(r => !r.Success));

            try { await cache.RefreshAsync(session.CompanyId); } catch { /* loggas i cachen */ }
            return saved;
        }
        finally { SendLock.Release(); }
    }
}
