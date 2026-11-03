using InvoiceManager.Api.Documents;

namespace InvoiceManager.Api.Contracts;

/// <summary>Represents a stored receipt and its current processing state.</summary>
public sealed record ReceiptResponse(
    Guid Id,
    string FileName,
    DateOnly? DocumentDate,
    string? Vendor,
    string? Currency,
    decimal? TotalAmount,
    ReceiptProcessingStatus Status,
    DateTimeOffset CreatedAtUtc);