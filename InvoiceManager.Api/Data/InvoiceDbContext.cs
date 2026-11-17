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
    public DbSet<Project> Projects => Set<Project>();

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
        receipts.HasIndex(receipt => receipt.ProcessingStatus);

        receipts.Property(receipt => receipt.FileName).HasMaxLength(255).IsRequired();
        receipts.Property(receipt => receipt.FilePath).HasMaxLength(2048).IsRequired();
        receipts.Property(receipt => receipt.ContentHash).HasMaxLength(64).IsFixedLength().IsRequired();
        receipts.Property(receipt => receipt.Vendor).HasMaxLength(256);
        receipts.Property(receipt => receipt.Currency).HasMaxLength(3).IsFixedLength();
        receipts.Property(receipt => receipt.TotalAmount).HasPrecision(18, 4);
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
        var projects = modelBuilder.Entity<Project>();
        projects.HasKey(project => project.Id);
        projects.HasAlternateKey(project => new { project.PropertyId, project.Id });
        projects.Property(project => project.Name).HasMaxLength(128).IsRequired();
        projects.Property(project => project.NormalizedName).HasMaxLength(128).IsRequired();
        projects.HasIndex(project => new { project.PropertyId, project.NormalizedName }).IsUnique();
        projects.HasOne<Property>().WithMany().HasForeignKey(project => project.PropertyId).OnDelete(DeleteBehavior.Restrict);
        lineItems.HasKey(lineItem => lineItem.Id);
        lineItems.Property(lineItem => lineItem.Description).HasMaxLength(1000).IsRequired();
        lineItems.Property(lineItem => lineItem.Quantity).HasPrecision(18, 4);
        lineItems.Property(lineItem => lineItem.UnitPrice).HasPrecision(18, 4);
        lineItems.Property(lineItem => lineItem.LineTotal).HasPrecision(18, 4);

        receipts
            .HasMany(receipt => receipt.LineItems)
            .WithOne(lineItem => lineItem.Receipt)
            .HasForeignKey(lineItem => new { lineItem.PropertyId, lineItem.ReceiptId })
            .HasPrincipalKey(receipt => new { receipt.PropertyId, receipt.Id })
            .OnDelete(DeleteBehavior.Cascade);
        lineItems.HasOne(item => item.Project).WithMany()
            .HasForeignKey(item => new { item.PropertyId, item.ProjectId })
            .HasPrincipalKey(project => new { project.PropertyId, project.Id }).OnDelete(DeleteBehavior.Restrict);

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

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<ReceiptLineItem>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.PropertyId == Guid.Empty))
        {
            var receipt = ChangeTracker.Entries<Receipt>()
                .Select(item => item.Entity).SingleOrDefault(item => item.Id == entry.Entity.ReceiptId);
            if (receipt is not null) entry.Entity.PropertyId = receipt.PropertyId;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}