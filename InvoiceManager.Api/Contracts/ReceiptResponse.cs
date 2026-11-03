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
    string? Category,
    string? Description,
    ReceiptProcessingStatus Status,
    string? ProcessingError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ReceiptLineItemResponse> LineItems);

/// <summary>Represents an item extracted from a receipt or invoice.</summary>
public sealed record ReceiptLineItemResponse(
    Guid Id,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? LineTotal,
    string? Category);