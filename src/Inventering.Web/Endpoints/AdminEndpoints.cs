using System.Globalization;
using System.Text;
using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Endpoints;

public record CreateSessionRequest(string Name);
public record SendRequest(bool Confirm, bool ZeroUncounted);
public record UserRequest(string UserName, string? DisplayName, string Role, string? Password, bool? IsActive);

public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin);

        g.MapGet("/status", (ArticleCache cache, IVismaGateway visma) => new
        {
            visma = visma.Name, articles = cache.Count, lastRefresh = cache.LastRefresh, lastError = cache.LastError,
        });

        // Anropar Visma (ADK) – kan ta några sekunder.
        g.MapGet("/visma-status", async (IVismaGateway visma) =>
        {
            try { return Results.Ok(await visma.GetStatusAsync()); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });

        g.MapPost("/articles/refresh", async (ArticleCache cache) =>
        {
            try { await cache.RefreshAsync(); return Results.Ok(new { count = cache.Count }); }
            catch (Exception ex) { return Results.Json(new { error = "Visma svarade inte: " + ex.Message }, statusCode: 502); }
        });

        g.MapGet("/sessions", async (AppDb db) =>
        {
            var sessions = await db.Sessions.AsNoTracking().OrderByDescending(s => s.CreatedAt).ToListAsync();
            var counts = await db.Lines.Where(l => !l.IsVoided).GroupBy(l => l.SessionId)
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
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Ange ett namn." });
            if (await db.Sessions.AnyAsync(s => s.Status == SessionStatus.Open || s.Status == SessionStatus.Locked))
                return Results.Json(new { error = "Det finns redan en pågående inventering. Skicka eller avbryt den först." }, statusCode: 409);
            try
            {
                var s = await inv.CreateAsync(req.Name, http.User.RequiredUserId());
                db.Log(http, "session.created", $"#{s.Id} {s.Name}");
                await db.SaveChangesAsync();
                return Results.Ok(new { s.Id });
            }
            catch (Exception ex) { return Results.Json(new { error = "Kunde inte läsa saldo från Visma: " + ex.Message }, statusCode: 502); }
        });

        g.MapPost("/sessions/{id:int}/{action:regex(^(lock|reopen|cancel)$)}", async (int id, string action, AppDb db, HttpContext http) =>
        {
            var s = await db.Sessions.FindAsync(id);
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

        g.MapGet("/sessions/{id:int}/summary", async (int id, AppDb db, InventoryService inv) =>
        {
            var s = await db.Sessions.FindAsync(id);
            if (s is null) return Results.NotFound();
            var (summary, uncounted) = await inv.SummarizeAsync(id);
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

        g.MapGet("/sessions/{id:int}/submissions", async (int id, AppDb db) =>
        {
            var subs = await db.Submissions.AsNoTracking().Where(s => s.SessionId == id).OrderBy(s => s.Id).ToListAsync();
            var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == id && l.SubmissionId != null).ToListAsync();
            var byUser = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            var bySub = lines.ToLookup(l => l.SubmissionId!.Value);
            return subs.Select(s => new
            {
                s.Id, s.SubmittedAt, user = byUser.GetValueOrDefault(s.UserId), s.LineCount, s.TotalQuantity, s.SignedWith,
                s.DeviceId, s.RemoteIp, s.Comment, s.Hash,
                // Kontrollsumman räknas om: falsk = raderna har ändrats i databasen efter signering.
                intact = SubmissionHasher.Compute(s, bySub[s.Id]) == s.Hash && bySub[s.Id].Count() == s.LineCount,
                voidedLines = bySub[s.Id].Count(l => l.IsVoided),
            });
        });

        g.MapGet("/sessions/{id:int}/lines", async (int id, string? articleNo, int? submissionId, AppDb db, ArticleCache cache) =>
        {
            var q = db.Lines.AsNoTracking().Where(l => l.SessionId == id);
            if (!string.IsNullOrEmpty(articleNo)) q = q.Where(l => l.ArticleNo == articleNo);
            if (submissionId is { } sid) q = q.Where(l => l.SubmissionId == sid);
            var lines = await q.ToListAsync();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            return lines.OrderBy(l => l.CountedAt).Select(l => new
            {
                l.Id, l.ArticleNo, name = cache.Get(l.ArticleNo)?.Name, l.Quantity, l.Location, l.Comment,
                l.CountedAt, l.ScannedCode, l.DeviceId, user = users.GetValueOrDefault(l.UserId), l.IsVoided, l.SubmissionId,
            });
        });

        g.MapPost("/lines/{lineId:guid}/{action:regex(^(void|unvoid)$)}", async (Guid lineId, string action, AppDb db, HttpContext http) =>
        {
            var line = await db.Lines.FindAsync(lineId);
            if (line is null) return Results.NotFound();
            var s = await db.Sessions.FindAsync(line.SessionId);
            if (s!.Status is SessionStatus.Sent or SessionStatus.Cancelled)
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
            try
            {
                var results = await inv.SendAsync(id, http.User.RequiredUserId(), req.ZeroUncounted);
                db.Log(http, "session.sent", $"#{id} ok={results.Count(r => r.Success)} fel={results.Count(r => !r.Success)} nollställ={req.ZeroUncounted}");
                await db.SaveChangesAsync();
                return Results.Ok(new { ok = results.Count(r => r.Success), failed = results.Count(r => !r.Success) });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (Exception ex) { return Results.Json(new { error = "Fel mot Visma: " + ex.Message }, statusCode: 502); }
        });

        g.MapGet("/sessions/{id:int}/results", async (int id, AppDb db) =>
            await db.SendResults.AsNoTracking().Where(r => r.SessionId == id).OrderBy(r => r.ArticleNo).ToListAsync());

        g.MapGet("/sessions/{id:int}/export.csv", async (int id, InventoryService inv) =>
        {
            var (summary, _) = await inv.SummarizeAsync(id);
            var sb = new StringBuilder("Artikelnr;Benämning;EAN;Saldo vid start;Räknat;Differens;Antal rader;Räknat av;Varningar\r\n");
            var c = CultureInfo.GetCultureInfo("sv-SE");
            foreach (var a in summary)
                sb.Append(string.Join(';', Csv(a.ArticleNo), Csv(a.Name), Csv(a.Ean), a.SnapshotQuantity.ToString(c), a.CountedQuantity.ToString(c),
                    a.Difference.ToString(c), a.LineCount, Csv(string.Join(", ", a.CountedBy)), Csv(string.Join(" | ", a.Warnings)))).Append("\r\n");
            // BOM så att Excel läser åäö rätt.
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"inventering-{id}.csv");
        });

        // Alla rader med full spårbarhet (vem, enhet, tid, inlämning, struken av).
        g.MapGet("/sessions/{id:int}/lines.csv", async (int id, AppDb db, ArticleCache cache) =>
        {
            var lines = await db.Lines.AsNoTracking().Where(l => l.SessionId == id).OrderBy(l => l.CountedAt).ToListAsync();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            var subs = await db.Submissions.AsNoTracking().Where(s => s.SessionId == id).ToDictionaryAsync(s => s.Id);
            var c = CultureInfo.GetCultureInfo("sv-SE");
            var sb = new StringBuilder("Rad-id;Artikelnr;Benämning;Antal;Plats;Kommentar;Skannad kod;Räknad;Användare;Enhet;Inlämning;Inlämnad;Signerad med;Struken;Struken av;Struken tid\r\n");
            foreach (var l in lines)
            {
                subs.TryGetValue(l.SubmissionId ?? 0, out var sub);
                sb.Append(string.Join(';', l.Id, Csv(l.ArticleNo), Csv(cache.Get(l.ArticleNo)?.Name), l.Quantity.ToString(c), Csv(l.Location), Csv(l.Comment),
                    Csv(l.ScannedCode), l.CountedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Csv(users.GetValueOrDefault(l.UserId)), Csv(l.DeviceId),
                    sub?.Id.ToString() ?? "UTKAST", sub?.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), sub?.SignedWith,
                    l.IsVoided ? "Ja" : "", l.VoidedByUserId is { } v ? Csv(users.GetValueOrDefault(v)) : "",
                    l.VoidedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"inventering-{id}-rader.csv");
        });

        // ---- Användare ----
        g.MapGet("/users", async (AppDb db) =>
            await db.Users.AsNoTracking().OrderBy(u => u.UserName).Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, role = u.Role.ToString(), u.IsActive,
                totpEnabled = u.TotpSecret != null, locked = u.LockedUntil > DateTimeOffset.UtcNow,
            }).ToListAsync());

        g.MapPost("/users", async (UserRequest req, AppDb db, PasswordService pw, IOptions<SecurityOptions> opt, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserName)) return Results.BadRequest(new { error = "Ange användarnamn." });
            if (!Enum.TryParse<UserRole>(req.Role, out var role)) return Results.BadRequest(new { error = "Ogiltig roll." });
            if (await db.Users.AnyAsync(u => u.UserName == req.UserName.Trim())) return Results.Conflict(new { error = "Användarnamnet finns redan." });
            var password = string.IsNullOrEmpty(req.Password) ? PasswordService.Generate(12) : req.Password;
            if (password.Length < opt.Value.MinPasswordLength) return Results.BadRequest(new { error = $"Lösenordet måste vara minst {opt.Value.MinPasswordLength} tecken." });
            var u = new User { UserName = req.UserName.Trim(), DisplayName = req.DisplayName?.Trim() ?? "", Role = role };
            u.PasswordHash = pw.Hash(u, password);
            db.Users.Add(u);
            db.Log(http, "user.created", $"{u.UserName} ({role})");
            await db.SaveChangesAsync();
            return Results.Ok(new { u.Id, password = string.IsNullOrEmpty(req.Password) ? password : null });
        });

        g.MapPut("/users/{id:int}", async (int id, UserRequest req, AppDb db, PasswordService pw, IOptions<SecurityOptions> opt, HttpContext http) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            var self = http.User.RequiredUserId() == id;
            if (req.DisplayName != null) u.DisplayName = req.DisplayName.Trim();
            if (Enum.TryParse<UserRole>(req.Role, out var role))
            {
                if (self && role != UserRole.Admin) return Results.BadRequest(new { error = "Du kan inte ta bort din egen admin-roll." });
                u.Role = role;
            }
            if (req.IsActive is { } active)
            {
                if (self && !active) return Results.BadRequest(new { error = "Du kan inte inaktivera dig själv." });
                u.IsActive = active;
            }
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (req.Password.Length < opt.Value.MinPasswordLength) return Results.BadRequest(new { error = $"Lösenordet måste vara minst {opt.Value.MinPasswordLength} tecken." });
                u.PasswordHash = pw.Hash(u, req.Password);
                u.LockedUntil = null; u.FailedLogins = 0;
            }
            db.Log(http, "user.updated", u.UserName);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/users/{id:int}/reset-totp", async (int id, AppDb db, HttpContext http) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            u.TotpSecret = null; u.LastTotpStep = null;
            db.Log(http, "user.totp_reset", u.UserName);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/audit", async (AppDb db) =>
        {
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName);
            var rows = await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(500).ToListAsync();
            return rows.Select(a => new { a.At, user = a.UserId is { } uid ? users.GetValueOrDefault(uid) : null, a.Action, a.Details, a.RemoteIp });
        });
    }

    private static string Csv(string? s) =>
        s is null ? "" : s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
