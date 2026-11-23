using System.Globalization;
using Inventering.Core.Domain;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Endpoints;

public record RuleRequest(string Name, string Type, string? Pattern, int QuantityDecimals, decimal QuantityMultiplier, bool IsActive, string? Description, bool AllCompanies = false);
public record AliasRequest(string Code, string ArticleNo, decimal Quantity, string? Note);
public record TestRequest(string Code, RuleRequest? Rule);

public static class BarcodeEndpoints
{
    public static void MapBarcodeAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/barcodes").RequireAuthorization(Policies.Admin).AddEndpointFilter<CompanyFilter>();

        // Regler för valt bolag + regler som gäller alla bolag.
        g.MapGet("/rules", async (AppDb db, HttpContext http) =>
        {
            var cid = http.CompanyId();
            return await db.BarcodeRules.AsNoTracking().Where(r => r.CompanyId == null || r.CompanyId == cid)
                .OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
        });

        g.MapPost("/rules", async (RuleRequest req, AppDb db, BarcodeService svc, HttpContext http) =>
        {
            var rule = new BarcodeRule { SortOrder = (await db.BarcodeRules.MaxAsync(r => (int?)r.SortOrder) ?? 0) + 10 };
            if (Apply(rule, req, http.CompanyId()) is { } err) return Results.BadRequest(new { error = err });
            db.BarcodeRules.Add(rule);
            db.Log(http, "barcode.rule_created", $"{rule.Name}: {rule.Type} {rule.Pattern}");
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok(new { rule.Id });
        });

        g.MapPut("/rules/{id:int}", async (int id, RuleRequest req, AppDb db, BarcodeService svc, HttpContext http) =>
        {
            var rule = await db.BarcodeRules.FindAsync(id);
            if (rule is null || rule.CompanyId is { } rc && rc != http.CompanyId()) return Results.NotFound();
            if (Apply(rule, req, http.CompanyId()) is { } err) return Results.BadRequest(new { error = err });
            db.Log(http, "barcode.rule_updated", $"{rule.Name}: {rule.Type} {rule.Pattern} aktiv={rule.IsActive}");
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok();
        });

        g.MapPost("/rules/{id:int}/move/{dir:regex(^(up|down)$)}", async (int id, string dir, AppDb db, BarcodeService svc, HttpContext http) =>
        {
            var cid = http.CompanyId();
            var rules = await db.BarcodeRules.Where(r => r.CompanyId == null || r.CompanyId == cid).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
            var i = rules.FindIndex(r => r.Id == id);
            var j = dir == "up" ? i - 1 : i + 1;
            if (i < 0) return Results.NotFound();
            if (j >= 0 && j < rules.Count) (rules[i], rules[j]) = (rules[j], rules[i]);
            for (var k = 0; k < rules.Count; k++) rules[k].SortOrder = (k + 1) * 10;
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok();
        });

        g.MapDelete("/rules/{id:int}", async (int id, AppDb db, BarcodeService svc, HttpContext http) =>
        {
            var rule = await db.BarcodeRules.FindAsync(id);
            if (rule is null || rule.CompanyId is { } rc && rc != http.CompanyId()) return Results.NotFound();
            db.BarcodeRules.Remove(rule);
            db.Log(http, "barcode.rule_deleted", rule.Name);
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok();
        });

        g.MapGet("/aliases", async (AppDb db, ArticleCache cache, HttpContext http) =>
        {
            var cid = http.CompanyId();
            return (await db.BarcodeAliases.AsNoTracking().Where(a => a.CompanyId == cid).OrderBy(a => a.ArticleNo).ToListAsync())
                .Select(a => new { a.Code, a.ArticleNo, name = cache.Get(cid, a.ArticleNo)?.Name, a.Quantity, a.Note });
        });

        g.MapPost("/aliases", async (AliasRequest req, AppDb db, ArticleCache cache, BarcodeService svc, HttpContext http) =>
        {
            var code = req.Code?.Trim() ?? "";
            if (code.Length is 0 or > 128) return Results.BadRequest(new { error = "Ange en streckkod." });
            var cid = http.CompanyId();
            var art = cache.Get(cid, req.ArticleNo?.Trim() ?? "");
            if (art is null) return Results.BadRequest(new { error = $"Artikel {req.ArticleNo} finns inte." });
            if (req.Quantity <= 0) return Results.BadRequest(new { error = "Antal måste vara större än 0." });
            if (cache.FindByEan(cid, CodeLookup.NormalizeEan(code)) is { } other)
                return Results.BadRequest(new { error = $"Koden är redan EAN för artikel {other.ArticleNo} i Visma." });
            var alias = await db.BarcodeAliases.FindAsync(cid, code);
            if (alias is null) db.BarcodeAliases.Add(alias = new BarcodeAlias { CompanyId = cid, Code = code });
            alias.ArticleNo = art.ArticleNo; alias.Quantity = req.Quantity; alias.Note = req.Note?.Trim();
            db.Log(http, "barcode.alias_saved", $"{code} → {art.ArticleNo} × {req.Quantity}");
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok();
        });

        g.MapDelete("/aliases/{code}", async (string code, AppDb db, BarcodeService svc, HttpContext http) =>
        {
            var alias = await db.BarcodeAliases.FindAsync(http.CompanyId(), code);
            if (alias is null) return Results.NotFound();
            db.BarcodeAliases.Remove(alias);
            db.Log(http, "barcode.alias_deleted", code);
            await db.SaveChangesAsync();
            await svc.ReloadAsync();
            return Results.Ok();
        });

        // Testa en kod mot sparade regler, eller mot en regel som håller på att redigeras.
        g.MapPost("/test", (TestRequest req, BarcodeService svc, HttpContext http) =>
        {
            var cid = http.CompanyId();
            ScanResult? r;
            if (req.Rule is { } rr)
            {
                var rule = new BarcodeRule();
                if (Apply(rule, rr, cid) is { } err) return Results.BadRequest(new { error = err });
                r = svc.Parse(cid, req.Code ?? "", [rule]);
            }
            else r = svc.Parse(cid, req.Code ?? "");

            var ais = BarcodeParser.ParseAis(req.Code ?? "");
            return Results.Ok(new
            {
                found = r != null,
                articleNo = r?.Article.ArticleNo, name = r?.Article.Name, quantity = r?.Quantity, via = r?.Via,
                gs1 = ais is not null && (ais.ContainsKey("01") || ais.ContainsKey("02")) ? ais : null,
            });
        });
    }

    private static string? Apply(BarcodeRule rule, RuleRequest req, int companyId)
    {
        rule.CompanyId = req.AllCompanies ? null : companyId;
        rule.Name = req.Name?.Trim() ?? "";
        rule.Type = Enum.TryParse<BarcodeRuleType>(req.Type, true, out var t) ? t : BarcodeRuleType.Regex;
        rule.Pattern = req.Pattern?.Trim() ?? "";
        rule.QuantityDecimals = req.QuantityDecimals;
        rule.QuantityMultiplier = req.QuantityMultiplier == 0 ? 1 : req.QuantityMultiplier;
        rule.IsActive = req.IsActive;
        rule.Description = req.Description?.Trim();
        return BarcodeParser.Validate(rule);
    }
}
