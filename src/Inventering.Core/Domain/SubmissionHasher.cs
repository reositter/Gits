using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventering.Core.Domain;

/// <summary>
/// Kontrollsumma över en inlämning och dess rader. Räknas om vid granskning – avviker den
/// har någon ändrat i databasen efter att användaren signerade.
/// Admin får stryka rader (IsVoided) utan att summan bryts; strykningen loggas separat.
/// </summary>
public static class SubmissionHasher
{
    public static string Compute(Submission s, IEnumerable<CountLine> lines)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("v1|").Append(s.Id).Append('|').Append(s.SessionId).Append('|').Append(s.UserId).Append('|')
          .Append(s.SubmittedAt.ToUnixTimeMilliseconds()).Append('|').Append(s.SignedWith).Append('\n');
        foreach (var l in lines.OrderBy(l => l.Id))
        {
            sb.Append(l.Id.ToString("N")).Append('|').Append(l.ArticleNo).Append('|')
              .Append(l.Quantity.ToString("0.######", inv)).Append('|').Append(l.Location).Append('|')
              .Append(l.Comment).Append('|').Append(l.CountedAt.ToUnixTimeMilliseconds()).Append('|')
              .Append(l.UserId).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
