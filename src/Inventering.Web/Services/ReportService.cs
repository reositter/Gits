using Inventering.Core.Domain;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

public record ReportArticle(string ArticleNo, string Name, string? Unit, decimal SnapshotQuantity, decimal CountedQuantity,
    decimal Difference, decimal? Price, decimal? DifferenceValue, string CountedBy, string Warnings, string? SendStatus);
public record ReportUncounted(string ArticleNo, string Name, string? Unit, decimal Stock, decimal? Price, decimal? Value);
public record ReportSubmission(int Id, string User, DateTimeOffset SubmittedAt, int Lines, decimal Total, string SignedWith, bool Intact, string Hash, string? Comment);
public record ReportLine(Guid Id, string ArticleNo, string Name, decimal Quantity, string? Location, string? Comment, string? ScannedCode,
    string? ScanVia, DateTimeOffset CountedAt, string User, string? Device, int? SubmissionId, bool Voided, string? VoidedBy);

public class ReportModel
{
    public required string CompanyName { get; init; }
    public required string CompanyPath { get; init; }
    public required InventorySession Session { get; init; }
    public string? CreatedBy { get; init; }
    public string? SentBy { get; init; }
    public required string GeneratedBy { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public required List<ReportArticle> Articles { get; init; }
    public required List<ReportUncounted> Uncounted { get; init; }
    public required List<ReportSubmission> Submissions { get; init; }
    public required List<ReportLine> Lines { get; init; }
    public int DraftLines { get; init; }
    public int SendOk { get; init; }
    public int SendFailed { get; init; }

    public int WithDifference => Articles.Count(a => a.Difference != 0);
    public decimal PlusQuantity => Articles.Where(a => a.Difference > 0).Sum(a => a.Difference);
    public decimal MinusQuantity => Articles.Where(a => a.Difference < 0).Sum(a => a.Difference);
    public decimal PlusValue => Articles.Where(a => a.DifferenceValue > 0).Sum(a => a.DifferenceValue ?? 0);
    public decimal MinusValue => Articles.Where(a => a.DifferenceValue < 0).Sum(a => a.DifferenceValue ?? 0);
    public decimal NetValue => PlusValue + MinusValue;
    public decimal CountedValue => Articles.Sum(a => a.CountedQuantity * (a.Price ?? 0));
    public bool HasPrices => Articles.Any(a => a.Price != null);
    public IEnumerable<string> Counters => Submissions.Select(s => s.User).Distinct();

    public static string StatusText(SessionStatus s) => s switch
    {
        SessionStatus.Open => "Öppen", SessionStatus.Locked => "Låst – under granskning",
        SessionStatus.Sent => "Skickad till Visma", SessionStatus.Cancelled => "Avbruten", _ => s.ToString(),
    };
}

/// <summary>Samlar allt som behövs för en inventeringsrapport (HTML och Excel använder samma modell).</summary>
public class ReportService(AppDb db, InventoryService inv, ArticleCache cache)
{
    public async Task<ReportModel> BuildAsync(InventorySession s, Company company, string generatedBy)
    {
        var (summary, uncounted) = await inv.SummarizeAsync(s);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserName : u.DisplayName);
        var prices = await db.Snapshots.AsNoTracking().Where(x => x.SessionId == s.Id)
            .ToDictionaryAsync(x => x.ArticleNo, x => x.PurchasePrice, StringComparer.OrdinalIgnoreCase);
        var results = (await db.SendResults.AsNoTracking().Where(r => r.SessionId == s.Id).ToListAsync())
            .GroupBy(r => r.ArticleNo, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var subs = await db.Submissions.AsNoTracking().Where(x => x.SessionId == s.Id).OrderBy(x => x.Id).ToListAsync();
        var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == s.Id).OrderBy(l => l.CountedAt).ToListAsync();
        var bySub = lines.Where(l => l.SubmissionId != null).ToLookup(l => l.SubmissionId!.Value);

        decimal? Price(string no) => prices.TryGetValue(no, out var p) && p != null ? p : cache.Get(s.CompanyId, no)?.PurchasePrice;

        return new ReportModel
        {
            CompanyName = company.Name, CompanyPath = company.CompanyPath, Session = s, GeneratedBy = generatedBy,
            CreatedBy = users.GetValueOrDefault(s.CreatedByUserId),
            SentBy = s.SentByUserId is { } sb ? users.GetValueOrDefault(sb) : null,
            Articles = summary.Select(a =>
            {
                var price = Price(a.ArticleNo);
                var res = results.GetValueOrDefault(a.ArticleNo);
                return new ReportArticle(a.ArticleNo, a.Name, cache.Get(s.CompanyId, a.ArticleNo)?.Unit, a.SnapshotQuantity, a.CountedQuantity,
                    a.Difference, price, price is { } p ? Math.Round(a.Difference * p, 2) : null,
                    string.Join(", ", a.CountedBy), string.Join("; ", a.Warnings),
                    res is null ? null : res.Success ? "OK" : "Fel: " + res.Message);
            }).ToList(),
            Uncounted = uncounted.Select(a =>
            {
                var price = Price(a.ArticleNo);
                return new ReportUncounted(a.ArticleNo, a.Name, a.Unit, a.StockQuantity, price, price is { } p ? Math.Round(a.StockQuantity * p, 2) : null);
            }).ToList(),
            Submissions = subs.Select(x => new ReportSubmission(x.Id, x.CountedByName ?? users.GetValueOrDefault(x.UserId) ?? $"#{x.UserId}", x.SubmittedAt, x.LineCount,
                x.TotalQuantity, x.SignedWith switch { "password" => "Lösenord", "excel-import" => "Excel-import" + (x.CountedByName is { } n ? $" ({n})" : ""), _ => "Admin-korrigering" },
                SubmissionHasher.Compute(x, bySub[x.Id]) == x.Hash && bySub[x.Id].Count() == x.LineCount, x.Hash, x.Comment)).ToList(),
            Lines = lines.Select(l => new ReportLine(l.Id, l.ArticleNo, cache.Get(s.CompanyId, l.ArticleNo)?.Name ?? "", l.Quantity, l.Location, l.Comment,
                l.ScannedCode, l.ScanVia, l.CountedAt, users.GetValueOrDefault(l.UserId) ?? $"#{l.UserId}", l.DeviceId, l.SubmissionId, l.IsVoided,
                l.VoidedByUserId is { } v ? users.GetValueOrDefault(v) : null)).ToList(),
            DraftLines = lines.Count(l => !l.IsVoided && l.SubmissionId == null),
            SendOk = results.Values.Count(r => r.Success),
            SendFailed = results.Values.Count(r => !r.Success),
        };
    }
}
