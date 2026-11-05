using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Data;

public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    private const int EmbeddingDimensions = 1536;

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<ReceiptLineItem> ReceiptLineItems => Set<ReceiptLineItem>();

    public DbSet<Property> Properties => Set<Property>();

    public DbSet<ImprovementExpense> ImprovementExpenses => Set<ImprovementExpense>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        var properties = modelBuilder.Entity<Property>();
        properties.HasKey(property => property.Id);
        properties.HasIndex(property => property.IsDefault)
            .IsUnique()
            .HasFilter("\"IsDefault\" = TRUE");
        properties.Property(property => property.Name).HasMaxLength(128).IsRequired();
        properties.Property(property => property.CreatedAtUtc).IsRequired();
        properties.Property(property => property.PropertyType).HasConversion<string>().HasMaxLength(32);

        var receipts = modelBuilder.Entity<Receipt>();
        receipts.HasKey(receipt => receipt.Id);
        receipts.HasAlternateKey(receipt => new { receipt.PropertyId, receipt.Id });
        receipts.HasIndex(receipt => new { receipt.PropertyId, receipt.ContentHash }).IsUnique();
        receipts.HasIndex(receipt => new { receipt.DocumentDate, receipt.Vendor });
        receipts.HasIndex(receipt => receipt.Category);
        receipts.HasIndex(receipt => receipt.ProcessingStatus);

        receipts.Property(receipt => receipt.FileName).HasMaxLength(255).IsRequired();
        receipts.Property(receipt => receipt.FilePath).HasMaxLength(2048).IsRequired();
        receipts.Property(receipt => receipt.ContentHash).HasMaxLength(64).IsFixedLength().IsRequired();
        receipts.Property(receipt => receipt.Vendor).HasMaxLength(256);
        receipts.Property(receipt => receipt.Currency).HasMaxLength(3).IsFixedLength();
        receipts.Property(receipt => receipt.TotalAmount).HasPrecision(18, 4);
        receipts.Property(receipt => receipt.Category).HasMaxLength(128);
        receipts.Property(receipt => receipt.Description).HasMaxLength(4000);
        receipts.Property(receipt => receipt.ExtractedMetadataJson).HasColumnType("jsonb");
        receipts.Property(receipt => receipt.ProcessingStatus).HasConversion<string>().HasMaxLength(24);
        receipts.Property(receipt => receipt.ProcessingError).HasMaxLength(4000);
        receipts.Property(receipt => receipt.EmbeddingModel).HasMaxLength(256);
        receipts.Property(receipt => receipt.EmbeddingModelVersion).HasMaxLength(128);
        receipts.Property(receipt => receipt.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
        receipts.Property(receipt => receipt.CreatedAtUtc).IsRequired();
        receipts.Property(receipt => receipt.UpdatedAtUtc).IsRequired();
        receipts.Property(receipt => receipt.ReviewVersion).IsConcurrencyToken();

        properties
            .HasMany(property => property.Receipts)
            .WithOne(receipt => receipt.Property)
            .HasForeignKey(receipt => receipt.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        var lineItems = modelBuilder.Entity<ReceiptLineItem>();
        lineItems.HasKey(lineItem => lineItem.Id);
        lineItems.Property(lineItem => lineItem.Description).HasMaxLength(1000).IsRequired();
        lineItems.Property(lineItem => lineItem.Quantity).HasPrecision(18, 4);
        lineItems.Property(lineItem => lineItem.UnitPrice).HasPrecision(18, 4);
        lineItems.Property(lineItem => lineItem.LineTotal).HasPrecision(18, 4);
        lineItems.Property(lineItem => lineItem.Category).HasMaxLength(128);

        receipts
            .HasMany(receipt => receipt.LineItems)
            .WithOne(lineItem => lineItem.Receipt)
            .HasForeignKey(lineItem => lineItem.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);

        var expenses = modelBuilder.Entity<ImprovementExpense>();
        expenses.HasKey(expense => expense.Id);
        expenses.Property(expense => expense.Description).HasMaxLength(1000).IsRequired();
        expenses.Property(expense => expense.Type).HasConversion<string>().HasMaxLength(32);
        expenses.Property(expense => expense.Amount).HasPrecision(18, 4);
        expenses.Property(expense => expense.Currency).HasMaxLength(3).IsFixedLength();
        expenses.Property(expense => expense.Notes).HasMaxLength(4000);
        expenses.Property(expense => expense.WorkDetails).HasMaxLength(4000);
        expenses.Property(expense => expense.ExclusionReason).HasMaxLength(1000);
        expenses.Property(expense => expense.RotAmount).HasPrecision(18, 4);
        expenses.Property(expense => expense.OtherCompensationAmount).HasPrecision(18, 4);
        expenses.HasOne(expense => expense.Receipt).WithMany(receipt => receipt.ImprovementExpenses)
            .HasForeignKey(expense => new { expense.PropertyId, expense.ReceiptId })
            .HasPrincipalKey(receipt => new { receipt.PropertyId, receipt.Id })
            .OnDelete(DeleteBehavior.Restrict);
        expenses.HasOne<Property>().WithMany().HasForeignKey(expense => expense.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}