namespace InvoiceManager.Api.Contracts;

public sealed record ExpenseReportRow(
    Guid ReceiptId, Guid PortionId, string FileName, string? Vendor, DateOnly ExpenseDate,
    string Description, string Classification, string Amount, string? WorkDetails,
    string? ExclusionReason, string? Notes);

public sealed record ExpenseReportTotal(int Year, string Classification, string Amount);

public sealed record UnresolvedExpenseReceipt(
    Guid ReceiptId, string FileName, string? Vendor, string? Currency,
    IReadOnlyList<string> Reasons, IReadOnlyList<ReceiptExpenseResponse> Portions);

public sealed record ExpenseReportResponse(
    Guid PropertyId, string PropertyName, SaleFactsRequest SaleFacts, DateTimeOffset GeneratedAtUtc,
    string FormatVersion, string Currency, int ReceiptCount, int ConfirmedReceiptCount,
    IReadOnlyList<ExpenseReportRow> Included, IReadOnlyList<ExpenseReportRow> Excluded,
    IReadOnlyList<UnresolvedExpenseReceipt> Unresolved, IReadOnlyList<ExpenseReportTotal> Totals,
    string IncludedTotal, string ExcludedTotal, string Html);
