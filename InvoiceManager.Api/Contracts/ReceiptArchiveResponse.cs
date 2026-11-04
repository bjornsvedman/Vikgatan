namespace InvoiceManager.Api.Contracts;

/// <summary>A page of receipts and archive-wide filter choices.</summary>
public sealed record ReceiptArchiveResponse(
    IReadOnlyList<ReceiptResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Currencies);