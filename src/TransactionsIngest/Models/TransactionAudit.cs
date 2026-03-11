using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionsIngest.Models;

public sealed class TransactionAudit
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(50)]
    public string TransactionId { get; set; } = null!;

    [MaxLength(20)]
    public string ChangeType { get; set; } = null!;

    public string? ChangeDetail { get; set; }

    public DateTime ChangedAt { get; set; }

    [ForeignKey(nameof(TransactionId))]
    public TransactionRecord Transaction { get; set; } = null!;
}
public static class ChangeTypes
{
    public const string Insert   = "Insert";
    public const string Update   = "Update";
    public const string Revoke   = "Revoke";
    public const string Finalize = "Finalize";
}
