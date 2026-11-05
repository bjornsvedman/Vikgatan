namespace InvoiceManager.Api.Contracts;

/// <summary>Filters and ordering for a year-grouped receipt archive page.</summary>
public sealed record ReceiptArchiveRequest(
    Guid PropertyId,
    int? Year = null,
    bool Undated = false,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? Category = null,
    bool Uncategorized = false,
    string? Vendor = null,
    string? Currency = null,
    string? Status = null,
    string SortBy = "date",
    string Direction = "desc",
    int Page = 1,
    int PageSize = 25);