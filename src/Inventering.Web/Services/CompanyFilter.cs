using Inventering.Core.Domain;
using Inventering.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventering.Web.Services;

/// <summary>
/// Kräver att ett bolag är valt och att användaren (fortfarande) har behörighet till det.
/// Bolaget läggs i HttpContext.Items och hämtas med <see cref="CompanyContext.Company"/>.
/// </summary>
public class CompanyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var companyId = http.User.CompanyId();
        if (companyId is null)
            return Results.Json(new { error = "Välj bolag först.", needCompany = true }, statusCode: 409);

        var db = http.RequestServices.GetRequiredService<AppDb>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == http.User.RequiredUserId());
        var company = user is null ? null : (await CompanyService.ForUserAsync(db, user)).FirstOrDefault(c => c.Id == companyId);
        if (company is null)
            return Results.Json(new { error = "Bolaget finns inte längre eller du saknar behörighet. Välj bolag igen.", needCompany = true }, statusCode: 409);

        http.Items[CompanyContext.Key] = company;
        return await next(ctx);
    }
}

public static class CompanyContext
{
    public const string Key = "inv.company";
    public static Company Company(this HttpContext http) =>
        http.Items[Key] as Company ?? throw new InvalidOperationException("Inget bolag i kontexten.");
    public static int CompanyId(this HttpContext http) => http.Company().Id;
}
