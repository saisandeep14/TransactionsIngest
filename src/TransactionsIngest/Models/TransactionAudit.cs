using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionsIngest.Models;

/// <summary>
/// Immutable audit entry appended whenever a <see cref="TransactionRecord"/>
/// is created, updated, revoked, or finalized.
/// </summary>
public sealed class TransactionAudit
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(50)]
    public string TransactionId { get; set; } = null!;

    /// <summary>Type of change: Insert | Update | Revoke | Finalize</summary>
    [MaxLength(20)]
    public string ChangeType { get; set; } = null!;

    /// <summary>
    /// Human-readable description of what changed.
    /// For updates this lists each field with old and new value.
    /// </summary>
    public string? ChangeDetail { get; set; }

    public DateTime ChangedAt { get; set; }

    // Navigation
    [ForeignKey(nameof(TransactionId))]
    public TransactionRecord Transaction { get; set; } = null!;
}

/// <summary>Well-known values for <see cref="TransactionAudit.ChangeType"/>.</summary>
public static class ChangeTypes
{
    public const string Insert   = "Insert";
    public const string Update   = "Update";
    public const string Revoke   = "Revoke";
    public const string Finalize = "Finalize";
}
