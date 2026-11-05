using Pgvector;

namespace InvoiceManager.Api.Documents;

public sealed class Receipt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public DateOnly? DocumentDate { get; set; }

    public string? Vendor { get; set; }

    public string? Currency { get; set; }

    public decimal? TotalAmount { get; set; }

    public string? Category { get; set; }

    public string? Description { get; set; }

    public string? ExtractedMetadataJson { get; set; }

    public ReceiptProcessingStatus ProcessingStatus { get; set; } = ReceiptProcessingStatus.Pending;

    public string? ProcessingError { get; set; }

    public string? EmbeddingModel { get; set; }

    public string? EmbeddingModelVersion { get; set; }

    public Vector? Embedding { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ReceiptLineItem> LineItems { get; set; } = [];

    public List<ImprovementExpense> ImprovementExpenses { get; set; } = [];

    public int ReviewVersion { get; set; }
}