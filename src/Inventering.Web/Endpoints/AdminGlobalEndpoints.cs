using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Endpoints;

public record UserRequest(string UserName, string? DisplayName, string Role, string? Password, bool? IsActive, List<int>? CompanyIds = null);
public record CompanyRequest(string Name, string CompanyPath, string? VismaName, string? VismaUser, string? VismaPassword, bool? ClearPassword, bool IsActive, int SortOrder);

/// <summary>Admin-funktioner som inte hör till ett visst bolag: bolag, användare, logg.</summary>
public static class AdminGlobalEndpoints
{
    public static void MapAdminGlobal(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin);

        // ---- Bolag ----
        g.MapGet("/companies", async (AppDb db, ArticleCache cache) =>
            (await db.Companies.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync()).Select(c => new
            {
                c.Id, c.Name, c.CompanyPath, c.VismaName, c.VismaUser, hasPassword = c.VismaPasswordProtected != null,
                c.IsActive, c.SortOrder, articles = cache.Count(c.Id), lastRefresh = cache.LastRefresh(c.Id), lastError = cache.LastError(c.Id),
            }));

        // Söker bolag i Visma (ADK: AvCompany.GetAvailableCompanies) och markerar vilka som redan är registrerade.
        g.MapGet("/companies/discover", async (IVismaGatewayFactory factory, AppDb db) =>
        {
            try
            {
                var found = await factory.DiscoverCompaniesAsync();
                var registered = await db.Companies.Select(c => c.CompanyPath).ToListAsync();
                return Results.Ok(found.Select(f => new
                {
                    f.Name, f.Directory, f.FullPath, f.DatabaseName,
                    registered = registered.Any(r => string.Equals(r.TrimEnd('\\', '/'), f.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)),
                }));
            }
            catch (Exception ex) { return Results.Json(new { error = "Kunde inte lista bolag i Visma: " + ex.Message }, statusCode: 502); }
        });

        g.MapPost("/companies", async (CompanyRequest req, AppDb db, CompanyService svc, HttpContext http) =>
        {
            var c = new Company();
            if (await Apply(c, req, db, svc) is { } err) return Results.BadRequest(new { error = err });
            db.Companies.Add(c);
            db.Log(http, "company.created", $"{c.Name} ({c.CompanyPath})");
            await db.SaveChangesAsync();
            return Results.Ok(new { c.Id });
        });

        g.MapPut("/companies/{id:int}", async (int id, CompanyRequest req, AppDb db, CompanyService svc, ArticleCache cache, HttpContext http) =>
        {
            var c = await db.Companies.FindAsync(id);
            if (c is null) return Results.NotFound();
            var oldPath = c.CompanyPath;
            if (await Apply(c, req, db, svc) is { } err) return Results.BadRequest(new { error = err });
            db.Log(http, "company.updated", $"{c.Name} ({c.CompanyPath}) aktiv={c.IsActive}");
            await db.SaveChangesAsync();
            if (!string.Equals(oldPath, c.CompanyPath, StringComparison.OrdinalIgnoreCase)) cache.Forget(id);
            return Results.Ok();
        });

        // Testar inloggning mot bolaget och läser in artiklarna.
        g.MapPost("/companies/{id:int}/test", async (int id, CompanyService svc, ArticleCache cache) =>
        {
            try
            {
                var status = await (await svc.GatewayAsync(id)).GetStatusAsync();
                await cache.RefreshAsync(id);
                return Results.Ok(new { status, articles = cache.Count(id) });
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });

        // ---- Användare ----
        g.MapGet("/users", async (AppDb db) =>
        {
            var access = (await db.UserCompanies.AsNoTracking().ToListAsync()).ToLookup(x => x.UserId, x => x.CompanyId);
            return (await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync()).Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, role = u.Role.ToString(), u.IsActive,
                totpEnabled = u.TotpSecret != null, locked = u.LockedUntil > DateTimeOffset.UtcNow,
                companyIds = access[u.Id].ToList(),
            });
        });

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
            await db.SaveChangesAsync();
            if (req.CompanyIds is { Count: > 0 } ids)
                db.UserCompanies.AddRange(ids.Distinct().Select(cid => new UserCompany { UserId = u.Id, CompanyId = cid }));
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
            // Bolagsbehörighet: null = oförändrad, tom lista = alla bolag.
            if (req.CompanyIds is { } ids)
            {
                await db.UserCompanies.Where(x => x.UserId == id).ExecuteDeleteAsync();
                db.UserCompanies.AddRange(ids.Distinct().Select(cid => new UserCompany { UserId = id, CompanyId = cid }));
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
            var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name);
            var rows = await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(500).ToListAsync();
            return rows.Select(a => new
            {
                a.At, user = a.UserId is { } uid ? users.GetValueOrDefault(uid) : null,
                company = a.CompanyId is { } cid ? companies.GetValueOrDefault(cid) : null,
                a.Action, a.Details, a.RemoteIp,
            });
        });
    }

    private static async Task<string?> Apply(Company c, CompanyRequest req, AppDb db, CompanyService svc)
    {
        var name = req.Name?.Trim() ?? "";
        var path = req.CompanyPath?.Trim() ?? "";
        if (name.Length == 0) return "Ange ett namn.";
        if (path.Length == 0) return "Ange företagsmappen i Visma.";
        if (await db.Companies.AnyAsync(x => x.Id != c.Id && x.CompanyPath == path)) return "Bolaget (företagsmappen) är redan registrerat.";
        if (await db.Companies.AnyAsync(x => x.Id != c.Id && x.Name == name)) return "Namnet används redan av ett annat bolag.";
        c.Name = name;
        c.CompanyPath = path;
        c.VismaName = req.VismaName?.Trim();
        c.VismaUser = string.IsNullOrWhiteSpace(req.VismaUser) ? null : req.VismaUser.Trim();
        if (req.ClearPassword == true) c.VismaPasswordProtected = null;
        else if (!string.IsNullOrEmpty(req.VismaPassword)) c.VismaPasswordProtected = svc.Protect(req.VismaPassword);
        c.IsActive = req.IsActive;
        c.SortOrder = req.SortOrder;
        return null;
    }
}
