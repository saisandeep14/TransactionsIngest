using Microsoft.EntityFrameworkCore;
using TransactionsIngest.Models;

namespace TransactionsIngest.Data;

public sealed class TransactionsDbContext : DbContext
{
    public TransactionsDbContext(DbContextOptions<TransactionsDbContext> options)
        : base(options) { }

    public DbSet<TransactionRecord> Transactions => Set<TransactionRecord>();
    public DbSet<TransactionAudit> Audits => Set<TransactionAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionRecord>(e =>
        {
            e.HasKey(t => t.TransactionId);
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)");
            e.Property(t => t.Status).HasConversion<string>();

            e.HasMany(t => t.Audits)
             .WithOne(a => a.Transaction)
             .HasForeignKey(a => a.TransactionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TransactionAudit>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedOnAdd();
        });
    }
}