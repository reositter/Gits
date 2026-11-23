using Inventering.Core.Domain;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Endpoints;

public record LineDto(Guid Id, string ArticleNo, string? ScannedCode, decimal Quantity, string? Location, string? Comment, DateTimeOffset? CountedAt, string? DeviceId);

public record EditLineRequest(decimal Quantity, string? Comment);
public record SubmitRequest(string Password, List<Guid> LineIds, string? DeviceId, string? Comment);

public static class CountEndpoints
{
    public static object ArticleDto(Article a) => new { a.ArticleNo, a.Name, a.Ean, a.Unit, a.IsStockItem };

    public static void MapCount(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization(Policies.Counter);

        g.MapGet("/sessions/open", async (AppDb db) =>
            await db.Sessions.Where(s => s.Status == SessionStatus.Open)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new { s.Id, s.Name, s.CreatedAt })
                .ToListAsync());

        g.MapGet("/articles/lookup", (string code, ArticleCache cache) =>
            cache.Find(code) is { } a ? Results.Ok(ArticleDto(a)) : Results.NotFound(new { error = $"Ingen artikel med kod \"{code}\"." }));

        g.MapGet("/articles/search", (string q, ArticleCache cache) =>
            cache.Search(q).Select(ArticleDto));

        // Tar emot en eller flera rader (offline-kön skickar flera). Idempotent på radens Id.
        g.MapPost("/sessions/{id:int}/lines", async (int id, List<LineDto> lines, AppDb db, ArticleCache cache, HttpContext http) =>
        {
            var session = await db.Sessions.FindAsync(id);
            if (session is null) return Results.NotFound(new { error = "Inventeringen finns inte." });
            var isAdmin = http.User.IsInRole(nameof(UserRole.Admin));
            // Admin får lägga korrigeringsrader i låst inventering.
            if (session.Status != SessionStatus.Open && !(isAdmin && session.Status == SessionStatus.Locked))
                return Results.Json(new { error = "Inventeringen är stängd för räkning." }, statusCode: 409);

            var userId = http.User.RequiredUserId();
            var ids = lines.Select(l => l.Id).ToList();
            var existing = await db.Lines.Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToListAsync();
            var accepted = new List<Guid>();
            var rejected = new List<object>();

            foreach (var l in lines)
            {
                if (existing.Contains(l.Id)) { accepted.Add(l.Id); continue; }
                var art = cache.Get(l.ArticleNo);
                string? error = l.Id == Guid.Empty ? "Saknar id"
                    : art is null ? $"Okänd artikel {l.ArticleNo}"
                    : !art.IsStockItem ? $"Artikel {l.ArticleNo} är inte lagerförd"
                    : l.Quantity < 0 && !isAdmin ? "Negativt antal"
                    : Math.Abs(l.Quantity) > 1_000_000 ? "Orimligt antal"
                    : null;
                if (error != null) { rejected.Add(new { l.Id, error }); continue; }

                db.Lines.Add(new CountLine
                {
                    Id = l.Id, SessionId = id, UserId = userId, DeviceId = Trim(l.DeviceId, 64),
                    ArticleNo = art!.ArticleNo, ScannedCode = Trim(l.ScannedCode, 64), Quantity = l.Quantity,
                    Location = Trim(l.Location, 64), Comment = Trim(l.Comment, 500),
                    CountedAt = DateTimeOffset.FromUnixTimeMilliseconds((l.CountedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds()),
                });
                accepted.Add(l.Id);
            }
            await db.SaveChangesAsync();

            // Admins korrigeringar i låst inventering lämnas in direkt (admin är redan inloggad med 2FA).
            if (isAdmin && session.Status == SessionStatus.Locked)
            {
                var newLines = db.ChangeTracker.Entries<CountLine>().Select(e => e.Entity)
                    .Where(x => accepted.Contains(x.Id) && x.SubmissionId == null).ToList();
                if (newLines.Count > 0)
                    await Submissions.CreateAsync(db, http, id, userId, newLines, "admin-correction", null, "Korrigering av admin");
            }
            return Results.Ok(new { accepted, rejected });
        });

        g.MapGet("/sessions/{id:int}/mylines", async (int id, AppDb db, ArticleCache cache, HttpContext http) =>
        {
            var userId = http.User.RequiredUserId();
            var lines = await db.Lines.Where(l => l.SessionId == id && l.UserId == userId && !l.IsVoided).ToListAsync();
            return lines.OrderByDescending(l => l.CountedAt).Select(l => new
            {
                l.Id, l.ArticleNo, name = cache.Get(l.ArticleNo)?.Name, l.Quantity, l.Location, l.Comment, l.CountedAt, l.ScannedCode, l.SubmissionId,
            });
        });

        // Ändra antal på en egen rad. Den gamla raden stryks och en ny skapas → full spårbarhet.
        g.MapPatch("/sessions/{id:int}/lines/{lineId:guid}", async (int id, Guid lineId, EditLineRequest req, AppDb db, HttpContext http) =>
        {
            var line = await db.Lines.FirstOrDefaultAsync(l => l.Id == lineId && l.SessionId == id);
            var session = await db.Sessions.FindAsync(id);
            if (line is null || session is null) return Results.NotFound();
            if (line.UserId != http.User.RequiredUserId()) return Results.Forbid();
            if (session.Status != SessionStatus.Open) return Results.Json(new { error = "Inventeringen är stängd." }, statusCode: 409);
            if (line.IsVoided) return Results.Json(new { error = "Raden är redan ändrad eller borttagen." }, statusCode: 409);
            if (line.SubmissionId != null) return Results.Json(new { error = "Raden är inlämnad och kan inte ändras. Kontakta admin." }, statusCode: 409);
            if (req.Quantity < 0 || req.Quantity > 1_000_000) return Results.BadRequest(new { error = "Ogiltigt antal." });

            var now = DateTimeOffset.UtcNow;
            line.IsVoided = true; line.VoidedAt = now; line.VoidedByUserId = line.UserId;
            var copy = new CountLine
            {
                Id = Guid.NewGuid(), SessionId = id, UserId = line.UserId, DeviceId = line.DeviceId,
                ArticleNo = line.ArticleNo, ScannedCode = line.ScannedCode, Quantity = req.Quantity,
                Location = line.Location, Comment = req.Comment ?? line.Comment, CountedAt = line.CountedAt,
            };
            db.Lines.Add(copy);
            await db.SaveChangesAsync();
            return Results.Ok(new { copy.Id });
        });

        // Lämna in (signera) sina utkastrader. Klienten skickar med exakt de rad-id:n användaren
        // ser på skärmen – stämmer de inte med serverns utkast avbryts inlämningen.
        g.MapPost("/sessions/{id:int}/submit", async (int id, SubmitRequest req, AppDb db, PasswordService pw,
            IOptions<SecurityOptions> opt, HttpContext http) =>
        {
            var session = await db.Sessions.FindAsync(id);
            if (session is null) return Results.NotFound();
            if (session.Status != SessionStatus.Open) return Results.Json(new { error = "Inventeringen är stängd för inlämning." }, statusCode: 409);
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();

            if (!pw.Verify(user, req.Password ?? "", out _))
            {
                user.FailedLogins++;
                if (user.FailedLogins >= opt.Value.MaxFailedLogins)
                {
                    user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(opt.Value.LockoutMinutes);
                    user.FailedLogins = 0;
                }
                db.Log(http, "submission.bad_password", user.UserName);
                await db.SaveChangesAsync();
                return Results.Json(new { error = "Fel lösenord." }, statusCode: 401);
            }
            user.FailedLogins = 0;

            var drafts = await db.Lines.Where(l => l.SessionId == id && l.UserId == user.Id && !l.IsVoided && l.SubmissionId == null).ToListAsync();
            if (drafts.Count == 0) return Results.BadRequest(new { error = "Det finns inga rader att lämna in." });
            var expected = drafts.Select(l => l.Id).ToHashSet();
            if (req.LineIds is null || !expected.SetEquals(req.LineIds))
                return Results.Json(new { error = "Raderna har ändrats sedan listan visades. Uppdatera och försök igen." }, statusCode: 409);

            var sub = await Submissions.CreateAsync(db, http, id, user.Id, drafts, "password", req.DeviceId, req.Comment?.Trim());
            return Results.Ok(new { sub.Id, sub.SubmittedAt, sub.LineCount, sub.TotalQuantity, sub.Hash });
        });

        g.MapGet("/sessions/{id:int}/mysubmissions", async (int id, AppDb db, HttpContext http) =>
        {
            var userId = http.User.RequiredUserId();
            var subs = await db.Submissions.AsNoTracking().Where(s => s.SessionId == id && s.UserId == userId).ToListAsync();
            return subs.OrderByDescending(s => s.SubmittedAt).Select(s => new { s.Id, s.SubmittedAt, s.LineCount, s.TotalQuantity, s.Hash });
        });

        // Räknaren kan ångra egna rader så länge inventeringen är öppen.
        g.MapDelete("/sessions/{id:int}/lines/{lineId:guid}", async (int id, Guid lineId, AppDb db, HttpContext http) =>
        {
            var line = await db.Lines.FirstOrDefaultAsync(l => l.Id == lineId && l.SessionId == id);
            var session = await db.Sessions.FindAsync(id);
            if (line is null || session is null) return Results.NotFound();
            if (line.UserId != http.User.RequiredUserId()) return Results.Forbid();
            if (session.Status != SessionStatus.Open) return Results.Json(new { error = "Inventeringen är stängd." }, statusCode: 409);
            if (line.SubmissionId != null) return Results.Json(new { error = "Raden är inlämnad och kan inte tas bort. Kontakta admin." }, statusCode: 409);
            line.IsVoided = true;
            line.VoidedAt = DateTimeOffset.UtcNow;
            line.VoidedByUserId = line.UserId;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
