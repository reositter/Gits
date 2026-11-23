namespace Inventering.Core.Domain;

public enum UserRole { Counter = 0, Admin = 1 }

public enum SessionStatus
{
    /// <summary>Räknare kan lägga till rader.</summary>
    Open = 0,
    /// <summary>Räkning stoppad, admin granskar.</summary>
    Locked = 1,
    /// <summary>Skickad till Visma (helt eller delvis).</summary>
    Sent = 2,
    Cancelled = 3,
}

public class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Base32 TOTP-hemlighet. Null = tvåfaktor ej aktiverad.</summary>
    public string? TotpSecret { get; set; }
    public long? LastTotpStep { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>En artikel som den ser ut i Visma, cachad lokalt för snabb skanning.</summary>
public class Article
{
    public string ArticleNo { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>EAN/streckkod. Saknas på vissa artiklar.</summary>
    public string? Ean { get; set; }
    public string? Unit { get; set; }
    public decimal StockQuantity { get; set; }
    /// <summary>Lagerförd artikel (bara sådana kan inventeras).</summary>
    public bool IsStockItem { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}

public class InventorySession
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public SessionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int CreatedByUserId { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int? SentByUserId { get; set; }
    public List<StockSnapshot> Snapshot { get; set; } = new();
    public List<CountLine> Lines { get; set; } = new();
}

/// <summary>Visma-saldo per artikel när inventeringen startades.</summary>
public class StockSnapshot
{
    public int SessionId { get; set; }
    public string ArticleNo { get; set; } = "";
    public decimal Quantity { get; set; }
}

/// <summary>En räkningsrad från en mobil. Sparas oförändrad; admin kan stryka men inte radera.</summary>
public class CountLine
{
    /// <summary>Genereras av mobilen → omsändning från offline-kön ger inga dubbletter.</summary>
    public Guid Id { get; set; }
    public int SessionId { get; set; }
    public int UserId { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset CountedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ArticleNo { get; set; } = "";
    /// <summary>Den kod som skannades/skrevs in (EAN eller artikelnr).</summary>
    public string? ScannedCode { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Valfri plats/hylla – hjälper att upptäcka dubbelräkning.</summary>
    public string? Location { get; set; }
    public string? Comment { get; set; }
    /// <summary>Null = utkast (ej inlämnat). Bara inlämnade rader räknas och skickas till Visma.</summary>
    public int? SubmissionId { get; set; }
    public bool IsVoided { get; set; }
    public int? VoidedByUserId { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
}

/// <summary>
/// En inlämning: användaren signerar (med lösenord) att raderna är hens räkning.
/// Raderna låses och en kontrollsumma (SHA-256) sparas så att ändringar i efterhand kan upptäckas.
/// </summary>
public class Submission
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public int UserId { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    /// <summary>Hur användaren bekräftade: "password" eller "admin-correction".</summary>
    public string SignedWith { get; set; } = "password";
    public string Hash { get; set; } = "";
    public string? RemoteIp { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Utfall per artikel när inventeringen skickades till Visma.</summary>
public class SendResult
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string ArticleNo { get; set; } = "";
    public decimal CountedQuantity { get; set; }
    public decimal SnapshotQuantity { get; set; }
    public decimal? StockBeforeWrite { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public string? RemoteIp { get; set; }
}
