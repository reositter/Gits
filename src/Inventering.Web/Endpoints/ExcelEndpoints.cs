using Inventering.Core.Domain;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Endpoints;

/// <summary>Inventering via Excel: skapa lista att skicka ut, läs in ifylld lista som en inlämning.</summary>
public static class ExcelEndpoints
{
    private const long MaxFileSize = 10 * 1024 * 1024;

    public static void MapExcel(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/sessions/{id:int}").RequireAuthorization(Policies.Admin).AddEndpointFilter<CompanyFilter>();

        g.MapGet("/countsheet.xlsx", async (int id, bool? includeStock, bool? onlyUncounted, string? from, string? to,
            AppDb db, ArticleCache cache, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            var articles = cache.All(s.CompanyId).AsEnumerable();
            if (onlyUncounted == true)
            {
                var counted = (await db.Lines.Where(l => l.SessionId == id && !l.IsVoided && l.SubmissionId != null)
                    .Select(l => l.ArticleNo).Distinct().ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                articles = articles.Where(a => !counted.Contains(a.ArticleNo));
            }
            var bytes = ExcelCountSheet.Create(http.Company(), s, articles,
                new CountSheetOptions(includeStock == true, onlyUncounted == true, Blank(from), Blank(to)));
            db.Log(http, "excel.countsheet_created", $"#{id}");
            await db.SaveChangesAsync();
            var safe = new string($"{http.Company().Name}-{s.Name}".Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Inventeringslista-{safe}.xlsx");
        });

        // commit=false: förhandsgranska. commit=true: läs in giltiga rader som en inlämning.
        g.MapPost("/import", async (int id, bool? commit, bool? allowDuplicate, AppDb db, ArticleCache cache, HttpContext http) =>
        {
            var s = await CountEndpoints.SessionAsync(db, http, id);
            if (s is null) return Results.NotFound();
            if (s.Status is not (SessionStatus.Open or SessionStatus.Locked))
                return Results.Json(new { error = "Inventeringen är avslutad – inget kan läsas in." }, statusCode: 409);
            if (!http.Request.HasFormContentType) return Results.BadRequest(new { error = "Ingen fil." });
            var form = await http.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Ingen fil." });
            if (file.Length > MaxFileSize) return Results.BadRequest(new { error = "Filen är för stor (max 10 MB)." });

            ImportResult result;
            try
            {
                await using var stream = file.OpenReadStream();
                result = ExcelCountSheet.Read(stream, Path.GetFileName(file.FileName), no => cache.Get(s.CompanyId, no));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }

            var warnings = new List<string>(result.Warnings);
            if (result.CompanyIdInFile is { } fc && fc != s.CompanyId)
                return Results.BadRequest(new { error = "Filen är skapad för ett annat bolag." });
            if (result.SessionIdInFile is { } fs && fs != s.Id)
                warnings.Add($"Filen skapades för inventering #{fs}, men läses in i #{s.Id}.");
            if (result.SessionIdInFile is null)
                warnings.Add("Filen är inte skapad från systemet – kolumnerna tolkades automatiskt.");
            var countedBy = (form["countedBy"].ToString() is { Length: > 0 } cb ? cb : result.CountedBy)?.Trim();
            if (string.IsNullOrEmpty(countedBy)) warnings.Add("\"Räknat av\" är inte ifyllt i filen.");

            var duplicate = await db.Submissions.Where(x => x.SessionId == id && x.SourceHash == result.FileHash)
                .Select(x => new { x.Id, x.SubmittedAt }).FirstOrDefaultAsync();
            if (duplicate != null) warnings.Add($"Exakt samma fil lästes redan in (inlämning #{duplicate.Id}).");

            var valid = result.Valid.ToList();
            if (commit == true)
            {
                if (duplicate != null && allowDuplicate != true)
                    return Results.Json(new { error = $"Filen är redan inläst (inlämning #{duplicate.Id})." }, statusCode: 409);
                if (valid.Count == 0) return Results.BadRequest(new { error = "Inga giltiga rader att läsa in." });

                var userId = http.User.RequiredUserId();
                var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var lines = valid.Select(r => new CountLine
                {
                    Id = Guid.NewGuid(), SessionId = id, UserId = userId, DeviceId = Truncate("Excel: " + result.FileName, 64),
                    ArticleNo = r.ArticleNo, ScannedCode = null, ScanVia = $"Excel-import rad {r.Row}", Quantity = r.Quantity,
                    Location = Truncate(r.Location, 64), Comment = Truncate(r.Comment, 500), CountedAt = at,
                }).ToList();
                db.Lines.AddRange(lines);
                await db.SaveChangesAsync();
                var sub = await Submissions.CreateAsync(db, http, id, userId, lines, "excel-import", null,
                    Truncate($"Excel: {result.FileName}" + (countedBy is null ? "" : $" – räknat av {countedBy}"), 500),
                    result.FileName, result.FileHash, countedBy);
                return Results.Ok(new { submissionId = sub.Id, imported = lines.Count, skipped = result.Invalid.Count() });
            }

            return Results.Ok(new
            {
                result.FileName, countedBy, valid = valid.Count, invalid = result.Invalid.Count(),
                total = valid.Sum(r => r.Quantity), articles = valid.Select(r => r.ArticleNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                duplicate = duplicate != null, warnings,
                rows = result.Rows.Select(r => new { r.Row, r.ArticleNo, r.Name, r.Quantity, r.Location, r.Comment, r.Error }),
            });
        }).DisableAntiforgery();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Truncate(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
