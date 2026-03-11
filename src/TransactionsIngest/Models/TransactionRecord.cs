using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionsIngest.Models;

/// <summary>
/// Canonical record for a single payment transaction.
/// TransactionId is the stable business key supplied by upstream systems.
/// </summary>
public sealed class TransactionRecord
{
    [Key]
    [MaxLength(50)]
    public string TransactionId { get; set; } = null!;

    /// <summary>
    /// Last 4 digits of the card number, stored for display purposes.
    /// The full PAN is never persisted.
    /// </summary>
    [MaxLength(4)]
    public string CardLast4 { get; set; } = null!;

    [MaxLength(20)]
    public string LocationCode { get; set; } = null!;

    [MaxLength(20)]
    public string ProductName { get; set; } = null!;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime TransactionTime { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Active;

    /// <summary>UTC timestamp of the first time this record was inserted.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last time any field on this record changed.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public ICollection<TransactionAudit> Audits { get; set; } = new List<TransactionAudit>();
}
