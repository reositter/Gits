using Inventering.Core.Domain;
using Inventering.Web.Data;

namespace Inventering.Web.Services;

public static class Submissions
{
    /// <summary>Skapar en inlämning, knyter raderna till den och sparar kontrollsumman.</summary>
    public static async Task<Submission> CreateAsync(AppDb db, HttpContext http, int sessionId, int userId,
        IReadOnlyList<CountLine> lines, string signedWith, string? deviceId, string? comment,
        string? sourceFile = null, string? sourceHash = null, string? countedByName = null)
    {
        var sub = new Submission
        {
            SessionId = sessionId, UserId = userId, DeviceId = deviceId, SignedWith = signedWith,
            LineCount = lines.Count, TotalQuantity = lines.Sum(l => l.Quantity),
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(), Comment = comment,
            SourceFile = sourceFile, SourceHash = sourceHash, CountedByName = countedByName,
            // Millisekundprecision så att kontrollsumman blir densamma efter lagring.
            SubmittedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync(); // ger Id
        foreach (var l in lines) l.SubmissionId = sub.Id;
        sub.Hash = SubmissionHasher.Compute(sub, lines);
        db.Log(http, "submission.created", $"#{sub.Id} session #{sessionId}: {sub.LineCount} rader, totalt {sub.TotalQuantity}, {signedWith}");
        await db.SaveChangesAsync();
        return sub;
    }
}
