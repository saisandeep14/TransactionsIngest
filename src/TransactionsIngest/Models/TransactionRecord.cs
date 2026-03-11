using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionsIngest.Models;

public sealed class TransactionRecord
{
    [Key]
    [MaxLength(50)]
    public string TransactionId { get; set; } = null!;

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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<TransactionAudit> Audits { get; set; } = new List<TransactionAudit>();
}
