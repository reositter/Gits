using System.Globalization;
using System.Text;
using Inventering.Core.Domain;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Endpoints;

public record CreateSessionRequest(string Name);
public record SendRequest(bool Confirm, bool ZeroUncounted);

/// <summary>Admin-funktioner för det valda bolaget.</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin).AddEndpointFilter<CompanyFilter>();

        g.MapGet("/status", (ArticleCache cache, CompanyService companies, HttpContext http) =>
        {
            var c = http.Company();
            return new
            {
                company = new { c.Id, c.Name, c.CompanyPath },
                articles = cache.Count(c.Id), lastRefresh = cache.LastRefresh(c.Id), lastError = cache.LastError(c.Id),
            };
        });

        // Anropar Visma (ADK) – kan ta några sekunder.
        g.MapGet("/visma-status", async (CompanyService companies, HttpContext http) =>
        {
            try { return Results.Ok(await (await companies.GatewayAsync(http.CompanyId())).GetStatusAsync()); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/articles/refresh", async (ArticleCache cache, HttpContext http) =>
        {
            try { await cache.RefreshAsync(http.CompanyId()); return Results.Ok(new { count = cache.Count(http.CompanyId()) }); }
            catch (Exception ex) { return Results.Json(new { error = "Visma svarade inte: " + ex.Message }, statusCode: 502); }
        });

        g.MapGet("/sessions", async (AppDb db, HttpContext http) =>
        {
            var cid = http.CompanyId();
            var sessions = await db.Sessions.AsNoTracking().Where(s => s.CompanyId == cid).OrderByDescending(s => s.Id).ToListAsync();
            var ids = sessions.Select(s => s.Id).ToList();
            var counts = await db.Lines.Where(l => ids.Contains(l.SessionId) && !l.IsVoided && l.SubmissionId != null).GroupBy(l => l.SessionId)
                .Select(x => new { x.Key, Lines = x.Count(), Articles = x.Select(l => l.ArticleNo).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key);
            return sessions.Select(s => new
            {
                s.Id, s.Name, status = s.Status.ToString(), s.CreatedAt, s.LockedAt, s.SentAt,
                lines = counts.GetValueOrDefault(s.Id)?.Lines ?? 0,
                articles = counts.GetValueOrDefault(s.Id)?.Articles ?? 0,
            });
        });

        g.MapPost("/sessions", async (CreateSessionRequest req, InventoryService inv, AppDb db, HttpContext http) =>
        {
            var cid = http.CompanyId();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Ange ett namn." });
            if (await db.Sessions.AnyAsync(s => s.CompanyId == cid && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Locked)))
                return Results.Json(new { error = "Det finns redan en pågående inventering i bolaget. Skicka eller avbryt den först." }, statusCode: 409);
            try
            {
                var s = await inv.CreateAsync(cid, req.Name, http.User.RequiredUserId());
                db.Log(http, "session.created", $"#{s.Id} {s.Name} ({http.Company().Name})");
                await db.SaveChangesAsync();
                return Results.Ok(new { s.Id });
            }
            catch (Exception ex) { return Results.Json(new { error = "Kunde inte läsa saldo från Visma: " + ex.Message }, statusCode: 502); }
        });

        g.MapPost("/sessions/{id:int}/{action:regex(^(lock|reopen|cancel)$)}", async (int id, string action, AppDb db, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            (SessionStatus from, SessionStatus to) = action switch
            {
                "lock" => (SessionStatus.Open, SessionStatus.Locked),
                "reopen" => (SessionStatus.Locked, SessionStatus.Open),
                _ => (s.Status, SessionStatus.Cancelled),
            };
            if (s.Status != from || s.Status is SessionStatus.Sent or SessionStatus.Cancelled)
                return Results.Json(new { error = $"Kan inte {action} när status är {s.Status}." }, statusCode: 409);
            s.Status = to;
            if (to == SessionStatus.Locked) s.LockedAt = DateTimeOffset.UtcNow;
            db.Log(http, "session." + action, $"#{id}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/sessions/{id:int}/summary", async (int id, AppDb db, InventoryService inv, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            var (summary, uncounted) = await inv.SummarizeAsync(s);
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            var drafts = await db.Lines.AsNoTracking().Where(l => l.SessionId == id && !l.IsVoided && l.SubmissionId == null)
                .GroupBy(l => l.UserId).Select(x => new { x.Key, Lines = x.Count() }).ToListAsync();
            return Results.Ok(new
            {
                drafts = drafts.Select(d => new { user = users.GetValueOrDefault(d.Key), d.Lines }),
                session = new { s.Id, s.Name, status = s.Status.ToString(), s.CreatedAt, s.LockedAt, s.SentAt },
                articles = summary.Select(a => new
                {
                    a.ArticleNo, a.Name, a.Ean, a.SnapshotQuantity, a.CurrentStock, a.CountedQuantity,
                    a.Difference, a.LineCount, a.CountedBy, a.Warnings,
                }),
                uncounted = uncounted.Select(a => new { a.ArticleNo, a.Name, a.StockQuantity }),
            });
        });

        g.MapGet("/sessions/{id:int}/submissions", async (int id, AppDb db, HttpContext http) =>
        {
            if (await CountEndpoints.SessionAsync(db, http, id) is null) return Results.NotFound();
            var subs = await db.Submissions.AsNoTracking().Where(s => s.SessionId == id).OrderBy(s => s.Id).ToListAsync();
            var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == id && l.SubmissionId != null).ToListAsync();
            var byUser = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserName : u.DisplayName);
            var bySub = lines.ToLookup(l => l.SubmissionId!.Value);
            return Results.Ok(subs.Select(s => new
            {
                s.Id, s.SubmittedAt, user = byUser.GetValueOrDefault(s.UserId), s.LineCount, s.TotalQuantity, s.SignedWith,
                s.DeviceId, s.RemoteIp, s.Comment, s.Hash, s.CountedByName, s.SourceFile,
                // Kontrollsumman räknas om: falsk = raderna har ändrats i databasen efter signering.
                intact = SubmissionHasher.Compute(s, bySub[s.Id]) == s.Hash && bySub[s.Id].Count() == s.LineCount,
                voidedLines = bySub[s.Id].Count(l => l.IsVoided),
            }));
        });

        g.MapGet("/sessions/{id:int}/lines", async (int id, string? articleNo, int? submissionId, AppDb db, ArticleCache cache, HttpContext http) =>
        {
            if (await CountEndpoints.SessionAsync(db, http, id) is null) return Results.NotFound();
            var cid = http.CompanyId();
            var q = db.Lines.AsNoTracking().Where(l => l.SessionId == id);
            if (!string.IsNullOrEmpty(articleNo)) q = q.Where(l => l.ArticleNo == articleNo);
            if (submissionId is { } sid) q = q.Where(l => l.SubmissionId == sid);
            var lines = await q.ToListAsync();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            return Results.Ok(lines.OrderBy(l => l.CountedAt).Select(l => new
            {
                l.Id, l.ArticleNo, name = cache.Get(cid, l.ArticleNo)?.Name, l.Quantity, l.Location, l.Comment,
                l.CountedAt, l.ScannedCode, l.ScanVia, l.DeviceId, user = users.GetValueOrDefault(l.UserId), l.IsVoided, l.SubmissionId,
            }));
        });

        g.MapPost("/lines/{lineId:guid}/{action:regex(^(void|unvoid)$)}", async (Guid lineId, string action, AppDb db, HttpContext http) =>
        {
            var line = await db.Lines.FindAsync(lineId);
            var s = line is null ? null : await CountEndpoints.SessionAsync(db, http, line.SessionId);
            if (line is null || s is null) return Results.NotFound();
            if (s.Status is SessionStatus.Sent or SessionStatus.Cancelled)
                return Results.Json(new { error = "Inventeringen är redan avslutad." }, statusCode: 409);
            line.IsVoided = action == "void";
            line.VoidedAt = line.IsVoided ? DateTimeOffset.UtcNow : null;
            line.VoidedByUserId = line.IsVoided ? http.User.RequiredUserId() : null;
            db.Log(http, "line." + action, $"{lineId} art {line.ArticleNo} antal {line.Quantity}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/sessions/{id:int}/send", async (int id, SendRequest req, InventoryService inv, AppDb db, HttpContext http) =>
        {
            if (!req.Confirm) return Results.BadRequest(new { error = "Bekräftelse saknas." });
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            try
            {
                var results = await inv.SendAsync(s, http.User.RequiredUserId(), req.ZeroUncounted);
                db.Log(http, "session.sent", $"#{id} ok={results.Count(r => r.Success)} fel={results.Count(r => !r.Success)} nollställ={req.ZeroUncounted}");
                await db.SaveChangesAsync();
                return Results.Ok(new { ok = results.Count(r => r.Success), failed = results.Count(r => !r.Success) });
            }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (Exception ex) { return Results.Json(new { error = "Fel mot Visma: " + ex.Message }, statusCode: 502); }
        });

        g.MapGet("/sessions/{id:int}/results", async (int id, AppDb db, HttpContext http) =>
            await CountEndpoints.SessionAsync(db, http, id) is null ? Results.NotFound()
                : Results.Ok(await db.SendResults.AsNoTracking().Where(r => r.SessionId == id).OrderBy(r => r.ArticleNo).ToListAsync()));

        g.MapGet("/sessions/{id:int}/export.csv", async (int id, InventoryService inv, AppDb db, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            var (summary, _) = await inv.SummarizeAsync(s);
            var sb = new StringBuilder("Artikelnr;Benämning;EAN;Saldo vid start;Räknat;Differens;Antal rader;Räknat av;Varningar\r\n");
            var c = CultureInfo.GetCultureInfo("sv-SE");
            foreach (var a in summary)
                sb.Append(string.Join(';', Csv(a.ArticleNo), Csv(a.Name), Csv(a.Ean), a.SnapshotQuantity.ToString(c), a.CountedQuantity.ToString(c),
                    a.Difference.ToString(c), a.LineCount, Csv(string.Join(", ", a.CountedBy)), Csv(string.Join(" | ", a.Warnings)))).Append("\r\n");
            return Results.File(Bom(sb), "text/csv", FileName(http, id, "artiklar"));
        });

        // ---- Rapporter ----
        g.MapGet("/sessions/{id:int}/report.html", async (int id, bool? onlyDiff, ReportService reports, AppDb db, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            var model = await reports.BuildAsync(s, http.Company(), http.User.Identity?.Name ?? "");
            return Results.Content(HtmlReport.Render(model, onlyDiff == true), "text/html; charset=utf-8");
        });

        g.MapGet("/sessions/{id:int}/report.xlsx", async (int id, ReportService reports, AppDb db, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            var model = await reports.BuildAsync(s, http.Company(), http.User.Identity?.Name ?? "");
            return Results.File(ExcelReport.Render(model), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                FileName(http, id, "rapport").Replace(".csv", ".xlsx"));
        });

        // Alla rader med full spårbarhet (vem, enhet, tid, inlämning, struken av).
        g.MapGet("/sessions/{id:int}/lines.csv", async (int id, AppDb db, ArticleCache cache, HttpContext http) =>
        {
            if (await CountEndpoints.SessionAsync(db, http, id) is null) return Results.NotFound();
            var cid = http.CompanyId();
            var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == id).OrderBy(l => l.CountedAt).ToListAsync();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            var subs = await db.Submissions.AsNoTracking().Where(s => s.SessionId == id).ToDictionaryAsync(s => s.Id);
            var c = CultureInfo.GetCultureInfo("sv-SE");
            var sb = new StringBuilder("Rad-id;Artikelnr;Benämning;Antal;Plats;Kommentar;Skannad kod;Tolkad via;Räknad;Användare;Enhet;Inlämning;Inlämnad;Signerad med;Struken;Struken av;Struken tid\r\n");
            foreach (var l in lines)
            {
                subs.TryGetValue(l.SubmissionId ?? 0, out var sub);
                sb.Append(string.Join(';', l.Id, Csv(l.ArticleNo), Csv(cache.Get(cid, l.ArticleNo)?.Name), l.Quantity.ToString(c), Csv(l.Location), Csv(l.Comment),
                    Csv(l.ScannedCode), Csv(l.ScanVia), l.CountedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Csv(users.GetValueOrDefault(l.UserId)), Csv(l.DeviceId),
                    sub?.Id.ToString() ?? "UTKAST", sub?.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), sub?.SignedWith,
                    l.IsVoided ? "Ja" : "", l.VoidedByUserId is { } v ? Csv(users.GetValueOrDefault(v)) : "",
                    l.VoidedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append("\r\n");
            }
            return Results.File(Bom(sb), "text/csv", FileName(http, id, "rader"));
        });
    }

    private static string FileName(HttpContext http, int id, string kind)
    {
        var name = new string(http.Company().Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return $"inventering-{name}-{id}-{kind}.csv";
    }

    // BOM så att Excel läser åäö rätt.
    private static byte[] Bom(StringBuilder sb) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();

    public static string Csv(string? s) =>
        s is null ? "" : s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
