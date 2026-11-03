using InvoiceManager.Api.Contracts;

namespace InvoiceManager.Api.Services;

public sealed record ReceiptReviewResult(
    ReceiptReviewStatus Status,
    ReceiptResponse? Receipt,
    IReadOnlyList<string> ValidationErrors);