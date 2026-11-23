using Inventering.Core.Domain;
using Inventering.Web.Data;

namespace Inventering.Web.Services;

public static class AuditExtensions
{
    public static void Log(this AppDb db, HttpContext http, string action, string? details = null) =>
        db.Audit.Add(new AuditEntry
        {
            UserId = http.User.UserId(),
            Action = action,
            Details = details,
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
}
