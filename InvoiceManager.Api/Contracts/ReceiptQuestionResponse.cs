using InvoiceManager.Api.Documents;

namespace InvoiceManager.Api.Contracts;

/// <summary>Contains an answer and the receipts used to support it.</summary>
public sealed record ReceiptQuestionResponse(
    string Answer,
    IReadOnlyList<ReceiptQuestionCitation> Sources,
    int? TotalSourceCount = null,
    ReceiptCalculation? Calculation = null);

/// <summary>Describes the actual SQL selection, totals, and every contributing receipt.</summary>
public sealed record ReceiptCalculation(
    int? Year,
    string? Vendor,
    string GroupBy,
    bool ReviewedOnly,
    IReadOnlyList<ReceiptCalculationTotal> Totals,
    IReadOnlyList<ReceiptCalculationReceipt> Receipts)
{
    public bool ItemBased { get; init; }
    public string? ProjectScope { get; init; }
    public IReadOnlyList<ReceiptCalculationItem> Items { get; init; } = [];
    public IReadOnlyList<string> CoverageWarnings { get; init; } = [];
}

public sealed record ReceiptCalculationItem(
    Guid Id, Guid ReceiptId, string FileName, string Description, Guid? ProjectId, string Project,
    string Currency, string? Amount, string GroupLabel);

/// <summary>A SQL-computed total; the invariant amount string preserves decimal precision.</summary>
public sealed record ReceiptCalculationTotal(string Label, string Currency, string Amount, int ReceiptCount)
{
    public Guid? ProjectId { get; init; }
    public bool UnassignedProject { get; init; }
}

/// <summary>A stored amount contributing to a calculation, independently of summary citation limits.</summary>
public sealed record ReceiptCalculationReceipt(
    Guid Id,
    string FileName,
    string? Vendor,
    DateOnly? DocumentDate,
    string Currency,
    string Amount,
    string GroupLabel);

/// <summary>Identifies a receipt cited by an answer.</summary>
public sealed record ReceiptQuestionCitation(
    Guid Id,
    string FileName,
    string? Vendor,
    DateOnly? DocumentDate,
    string? Currency,
    decimal? TotalAmount,
    ReceiptProcessingStatus Status)
{
    public IReadOnlyList<string> SourceProjects { get; init; } = [];
    public IReadOnlyList<ReceiptCalculationItem> MatchedItems { get; init; } = [];
}