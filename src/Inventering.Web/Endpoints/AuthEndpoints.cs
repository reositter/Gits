using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using Inventering.Core.Domain;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Endpoints;

public record LoginRequest(string UserName, string Password, string? TotpCode);
public record CodeRequest(string Code);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record SelectCompanyRequest(int CompanyId);
public record DisableTotpRequest(string Password, string Code);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, AppDb db, PasswordService pw, IOptions<SecurityOptions> opt, HttpContext http) =>
        {
            var o = opt.Value;
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == req.UserName.Trim());
            var now = DateTimeOffset.UtcNow;

            if (user is null || !user.IsActive)
            {
                // Samma svar som fel lösenord → avslöjar inte vilka användarnamn som finns.
                db.Log(http, "login.failed", $"okänd/inaktiv: {req.UserName}");
                await db.SaveChangesAsync();
                return Results.Json(new { error = "Fel användarnamn eller lösenord." }, statusCode: 401);
            }
            if (user.LockedUntil > now)
                return Results.Json(new { error = $"Kontot är spärrat till {user.LockedUntil:HH:mm} efter för många felförsök." }, statusCode: 423);

            if (!pw.Verify(user, req.Password ?? "", out var rehash))
            {
                user.FailedLogins++;
                if (user.FailedLogins >= o.MaxFailedLogins)
                {
                    user.LockedUntil = now.AddMinutes(o.LockoutMinutes);
                    user.FailedLogins = 0;
                    db.Log(http, "login.locked", user.UserName);
                }
                db.Log(http, "login.failed", user.UserName);
                await db.SaveChangesAsync();
                return Results.Json(new { error = "Fel användarnamn eller lösenord." }, statusCode: 401);
            }

            var mfa = false;
            if (user.TotpSecret != null)
            {
                if (string.IsNullOrWhiteSpace(req.TotpCode))
                    return Results.Ok(new { needTotp = true });
                if (!Totp.Verify(user.TotpSecret, req.TotpCode, user.LastTotpStep, out var step))
                {
                    user.FailedLogins++;
                    if (user.FailedLogins >= o.MaxFailedLogins) { user.LockedUntil = now.AddMinutes(o.LockoutMinutes); user.FailedLogins = 0; }
                    db.Log(http, "login.totp_failed", user.UserName);
                    await db.SaveChangesAsync();
                    return Results.Json(new { error = "Fel engångskod.", needTotp = true }, statusCode: 401);
                }
                user.LastTotpStep = step;
                mfa = true;
            }

            if (rehash) user.PasswordHash = pw.Hash(user, req.Password!);
            user.FailedLogins = 0;
            user.LockedUntil = null;
            db.Log(http, "login.ok", user.UserName);
            await db.SaveChangesAsync();

            // Har användaren bara ett bolag väljs det direkt.
            var companies = await CompanyService.ForUserAsync(db, user);
            await SignIn(http, user, mfa, o, companies.Count == 1 ? companies[0].Id : null);
            return Results.Ok(new { ok = true, mustSetupTotp = MustSetupTotp(user, o) });
        }).RequireRateLimiting("login");

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        g.MapGet("/me", async (HttpContext http, AppDb db, IOptions<SecurityOptions> opt) =>
        {
            var id = http.User.UserId();
            var user = id is null ? null : await db.Users.FindAsync(id.Value);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var companies = await CompanyService.ForUserAsync(db, user);
            var current = companies.FirstOrDefault(c => c.Id == http.User.CompanyId());
            return Results.Ok(new
            {
                company = current is null ? null : new { current.Id, current.Name },
                companies = companies.Select(c => new { c.Id, c.Name }),
                user.Id, user.UserName, user.DisplayName,
                role = user.Role.ToString(),
                totpEnabled = user.TotpSecret != null,
                totpRequired = TotpRequired(user, opt.Value),
                mfa = http.User.HasClaim(Claims.Mfa, "1"),
                mustSetupTotp = MustSetupTotp(user, opt.Value),
            });
        }).RequireAuthorization();

        g.MapPost("/totp/setup", async (HttpContext http, AppDb db, IOptions<SecurityOptions> opt) =>
        {
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();
            if (user.TotpSecret != null) return Results.BadRequest(new { error = "Tvåfaktor är redan aktiverad. Be en admin återställa den." });
            var secret = Totp.NewSecret();
            // Hemligheten hålls i en kortlivad krypterad cookie tills användaren bekräftat med en kod.
            http.Response.Cookies.Append("totp_pending", Protect(http, secret), new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromMinutes(10),
            });
            return Results.Ok(new { secret, uri = Totp.Uri(opt.Value.TotpIssuer, user.UserName, secret) });
        }).RequireAuthorization();

        g.MapPost("/totp/confirm", async (CodeRequest req, HttpContext http, AppDb db, IOptions<SecurityOptions> opt) =>
        {
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();
            var secret = http.Request.Cookies.TryGetValue("totp_pending", out var c) ? Unprotect(http, c) : null;
            if (secret is null) return Results.BadRequest(new { error = "Ingen pågående aktivering – börja om." });
            if (!Totp.Verify(secret, req.Code, null, out var step)) return Results.BadRequest(new { error = "Fel kod. Kontrollera att klockan i mobilen är rätt." });
            user.TotpSecret = secret;
            user.LastTotpStep = step;
            db.Log(http, "totp.enabled", user.UserName);
            await db.SaveChangesAsync();
            http.Response.Cookies.Delete("totp_pending");
            await SignIn(http, user, true, opt.Value, http.User.CompanyId());
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Stäng av egen tvåfaktor – bara om den inte krävs. Kräver lösenord och en giltig kod.
        g.MapPost("/totp/disable", async (DisableTotpRequest req, HttpContext http, AppDb db, PasswordService pw, IOptions<SecurityOptions> opt) =>
        {
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();
            if (user.TotpSecret is null) return Results.BadRequest(new { error = "Tvåfaktor är inte påslagen." });
            if (TotpRequired(user, opt.Value)) return Results.Json(new { error = "Tvåfaktor krävs för ditt konto och kan inte stängas av." }, statusCode: 403);
            if (!pw.Verify(user, req.Password ?? "", out _)) return Results.BadRequest(new { error = "Fel lösenord." });
            if (!Totp.Verify(user.TotpSecret, req.Code, user.LastTotpStep, out _)) return Results.BadRequest(new { error = "Fel kod." });
            user.TotpSecret = null; user.LastTotpStep = null;
            db.Log(http, "totp.disabled", user.UserName);
            await db.SaveChangesAsync();
            await SignIn(http, user, false, opt.Value, http.User.CompanyId());
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Välj bolag att jobba i. Sparas i inloggningscookien.
        g.MapPost("/company", async (SelectCompanyRequest req, HttpContext http, AppDb db, IOptions<SecurityOptions> opt) =>
        {
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();
            var companies = await CompanyService.ForUserAsync(db, user);
            var company = companies.FirstOrDefault(c => c.Id == req.CompanyId);
            if (company is null) return Results.Json(new { error = "Du har inte behörighet till bolaget." }, statusCode: 403);
            await SignIn(http, user, http.User.HasClaim(Claims.Mfa, "1"), opt.Value, company.Id);
            db.Log(http, "company.selected", company.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { company.Id, company.Name });
        }).RequireAuthorization();

        g.MapPost("/password", async (ChangePasswordRequest req, HttpContext http, AppDb db, PasswordService pw, IOptions<SecurityOptions> opt) =>
        {
            var user = await db.Users.FindAsync(http.User.RequiredUserId());
            if (user is null) return Results.Unauthorized();
            if (!pw.Verify(user, req.CurrentPassword, out _)) return Results.BadRequest(new { error = "Nuvarande lösenord är fel." });
            if ((req.NewPassword?.Length ?? 0) < opt.Value.MinPasswordLength)
                return Results.BadRequest(new { error = $"Lösenordet måste vara minst {opt.Value.MinPasswordLength} tecken." });
            user.PasswordHash = pw.Hash(user, req.NewPassword!);
            db.Log(http, "password.changed", user.UserName);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();
    }

    /// <summary>Krävs tvåfaktor för användaren – globalt (alla/admin) eller per användare.</summary>
    public static bool TotpRequired(User u, SecurityOptions o) =>
        u.RequireTotp || o.RequireTotpForAll || (o.RequireTotpForAdmin && u.Role == UserRole.Admin);

    public static bool MustSetupTotp(User u, SecurityOptions o) => u.TotpSecret == null && TotpRequired(u, o);

    private static async Task SignIn(HttpContext http, User user, bool mfa, SecurityOptions o, int? companyId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            // Stämpel så att ändrad roll/inaktivering/lösenord slår igenom (kontrolleras i OnValidatePrincipal).
            new("stamp", Stamp(user)),
        };
        if (mfa) claims.Add(new Claim(Claims.Mfa, "1"));
        if (user.RequireTotp) claims.Add(new Claim(Claims.TotpRequired, "1"));
        if (companyId is { } cid) claims.Add(new Claim(Claims.Company, cid.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(o.SessionHours) });
    }

    public static string Stamp(User u) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{u.PasswordHash}|{u.Role}|{u.IsActive}|{u.TotpSecret}|{u.RequireTotp}")))[..16];

    private static string Protect(HttpContext http, string value) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("totp").Protect(value);

    private static string? Unprotect(HttpContext http, string value)
    {
        try
        {
            return http.RequestServices.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("totp").Unprotect(value);
        }
        catch { return null; }
    }
}
