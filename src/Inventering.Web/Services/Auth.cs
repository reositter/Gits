using System.Security.Claims;
using System.Security.Cryptography;
using Inventering.Core.Domain;
using Microsoft.AspNetCore.Identity;
using OtpNet;

namespace Inventering.Web.Services;

public static class Claims
{
    public const string Mfa = "mfa";
    public const string Company = "company";
    /// <summary>Valt bolag (sätts när användaren väljer bolag efter inloggning).</summary>
    public static int? CompanyId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(Company), out var id) ? id : null;
    public static int? UserId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public static int RequiredUserId(this ClaimsPrincipal p) => p.UserId() ?? throw new UnauthorizedAccessException();
}

public static class Policies
{
    public const string Counter = "counter";
    public const string Admin = "admin";
}

public class SecurityOptions
{
    /// <summary>Kräv TOTP för administratörer (rekommenderas alltid).</summary>
    public bool RequireTotpForAdmin { get; set; } = true;
    /// <summary>Kräv TOTP för alla användare – slå på om tjänsten nås utanför LAN.</summary>
    public bool RequireTotpForAll { get; set; }
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int SessionHours { get; set; } = 12;
    public int MinPasswordLength { get; set; } = 10;
    public string TotpIssuer { get; set; } = "Inventering";
}

public class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();
    public string Hash(User u, string password) => _hasher.HashPassword(u, password);
    public bool Verify(User u, string password, out bool needsRehash)
    {
        var r = _hasher.VerifyHashedPassword(u, u.PasswordHash, password);
        needsRehash = r == PasswordVerificationResult.SuccessRehashNeeded;
        return r != PasswordVerificationResult.Failed;
    }

    public static string Generate(int length = 16)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, chars, (span, c) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = c[RandomNumberGenerator.GetInt32(c.Length)];
        });
    }
}

public static class Totp
{
    public static string NewSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public static string Uri(string issuer, string account, string secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6&period=30";

    /// <summary>Verifierar kod (±1 tidssteg). Samma kod kan inte återanvändas (replay-skydd).</summary>
    public static bool Verify(string secret, string? code, long? lastStep, out long step)
    {
        step = 0;
        code = code?.Replace(" ", "");
        if (string.IsNullOrEmpty(code) || code.Length != 6) return false;
        var totp = new OtpNet.Totp(Base32Encoding.ToBytes(secret));
        if (!totp.VerifyTotp(code, out step, new VerificationWindow(1, 1))) return false;
        return lastStep is null || step > lastStep;
    }
}
