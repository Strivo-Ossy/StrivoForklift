using Microsoft.EntityFrameworkCore;
using StrivoForklift.Models;

namespace StrivoForklift.Data;

/// <summary>
/// EF Core database context for bank transaction storage.
/// </summary>
public class ForkliftDbContext : DbContext
{
    public ForkliftDbContext(DbContextOptions<ForkliftDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions", "dbo");
            entity.HasKey(e => e.TransactionId);
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id").IsRequired();
            entity.Property(e => e.AccountId).HasColumnName("account_id").HasMaxLength(100);
            entity.Property(e => e.Source).HasMaxLength(255);
            entity.Property(e => e.EventTs).HasColumnName("event_ts");
            entity.Property(e => e.InsertionTime).HasColumnName("insertion_time").IsRequired();
            entity.Property(e => e.OriginalJson).HasColumnName("original_json");
            entity.HasIndex(e => e.AccountId)
                  .HasDatabaseName("IX_transactions_account_id");
        });
    }
}
