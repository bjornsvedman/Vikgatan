using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Data;

public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    private const int EmbeddingDimensions = 1536;

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<ReceiptLineItem> ReceiptLineItems => Set<ReceiptLineItem>();

    public DbSet<Property> Properties => Set<Property>();

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

        var receipts = modelBuilder.Entity<Receipt>();
        receipts.HasKey(receipt => receipt.Id);
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
    }
}