using System.Threading.RateLimiting;
using Inventering.Core.Domain;
using Inventering.Core.Visma;
using Inventering.Web.Data;
using Inventering.Web.Endpoints;
using Inventering.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Som Windows-tjänst är arbetskatalogen System32 – utgå från programmets katalog.
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "Inventering");

var dataDir = builder.Configuration["DataDirectory"] is { Length: > 0 } d
    ? Environment.ExpandEnvironmentVariables(d)
    : OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Inventering")
        : Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "inventory.db")}"));
// Nycklar för cookies sparas på disk så att inloggningar överlever omstart av tjänsten.
var dp = builder.Services.AddDataProtection().SetApplicationName("Inventering")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

// ---- Visma (en gateway per bolag) ----
var vismaMode = builder.Configuration["Visma:Mode"] ?? "Fake";
if (vismaMode.Equals("Adk", StringComparison.OrdinalIgnoreCase))
{
    var adk = builder.Configuration.GetSection("Visma:Adk").Get<AdkOptions>() ?? new AdkOptions();
    builder.Services.AddSingleton(new AdkBridgeRunner(adk));
    builder.Services.AddSingleton<IVismaGatewayFactory>(sp => new AdkGatewayFactory(
        sp.GetRequiredService<AdkBridgeRunner>(),
        c => sp.GetRequiredService<CompanyService>().Unprotect(c)));
}
else builder.Services.AddSingleton<IVismaGatewayFactory, FakeVismaGatewayFactory>();

builder.Services.AddSingleton<CompanyService>();
builder.Services.AddSingleton<ArticleCache>();
builder.Services.AddSingleton<BarcodeService>();
builder.Services.AddHostedService<ArticleRefreshService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddSingleton<PasswordService>();

// ---- Inloggning ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "inv_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        // API:t ska svara 401/403, inte omdirigera.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        // Inaktiverad användare, ändrad roll eller nytt lösenord loggar ut befintliga sessioner.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var id = ctx.Principal?.UserId();
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var user = id is null ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
            if (user is null || !user.IsActive || ctx.Principal!.FindFirst("stamp")?.Value != AuthEndpoints.Stamp(user))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Counter, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
    {
        var o = (ctx.Resource as HttpContext)?.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value ?? new();
        return !o.RequireTotpForAll || ctx.User.HasClaim(Claims.Mfa, "1");
    }))
    .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)).RequireAssertion(ctx =>
    {
        var o = (ctx.Resource as HttpContext)?.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value ?? new();
        return !(o.RequireTotpForAdmin || o.RequireTotpForAll) || ctx.User.HasClaim(Claims.Mfa, "1");
    }));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// ---- Databas + första admin ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    if (!db.Users.Any())
    {
        var pw = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var password = PasswordService.Generate(16);
        var admin = new User { UserName = "admin", DisplayName = "Administratör", Role = UserRole.Admin };
        admin.PasswordHash = pw.Hash(admin, password);
        db.Users.Add(admin);
        db.SaveChanges();
        var file = Path.Combine(dataDir, "forsta-admin-losenord.txt");
        File.WriteAllText(file, $"Användare: admin\r\nLösenord: {password}\r\n\r\nByt lösenord och radera denna fil efter första inloggningen.\r\n");
        app.Logger.LogWarning("Första admin-kontot skapat. Lösenordet finns i {File}", file);
    }

    if (!db.Companies.Any())
    {
        // Första start: ta bolag från tidigare konfiguration (Visma:Adk:CompanyPath) eller skapa demobolag.
        var legacyPath = app.Configuration["Visma:Adk:CompanyPath"];
        if (vismaMode.Equals("Adk", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(legacyPath))
            db.Companies.Add(new Company { Name = Path.GetFileName(legacyPath.TrimEnd('\\', '/')), CompanyPath = legacyPath });
        else if (!vismaMode.Equals("Adk", StringComparison.OrdinalIgnoreCase))
        {
            db.Companies.Add(new Company { Name = "Demo AB", CompanyPath = @"C:\Demo\Företag\FTG1", VismaName = "Demo AB", SortOrder = 1 });
            db.Companies.Add(new Company { Name = "Testbygg AB", CompanyPath = @"C:\Demo\Företag\FTG2", VismaName = "Testbygg AB", SortOrder = 2 });
        }
        db.SaveChanges();
    }
}
await app.Services.GetRequiredService<BarcodeService>().ReloadAsync();

// ---- Säkerhetshuvuden ----
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
    h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();

// CSRF-skydd: alla ändrande API-anrop måste ha ett eget huvud, vilket en annan webbplats
// inte kan skicka utan CORS-godkännande. Kombineras med SameSite=Strict-cookie.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Headers["X-Requested-With"] != "inventering")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Ogiltig begäran." });
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
app.MapAuth();
app.MapCount();
app.MapAdmin();
app.MapAdminGlobal();
app.MapBarcodeAdmin();

app.Run();

public partial class Program;
