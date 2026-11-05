using InvoiceManager.Api.Documents;
using System.Text.Json.Serialization;

namespace InvoiceManager.Api.Contracts;

public sealed record SaleFactsRequest(PropertyType? PropertyType, DateOnly? AcquisitionDate, DateOnly? SaleDate);

public sealed record DraftExpenseRequest(
    Guid ReceiptId,
    string? Description,
    DateOnly? ExpenseDate,
    ImprovementType Type,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? Amount,
    string? Currency,
    string? Notes);

public sealed record ReceiptExpenseRequest(
    Guid? Id,
    string? Description,
    DateOnly? ExpenseDate,
    ImprovementType Type,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? Amount,
    string? Currency,
    string? Notes,
    string? WorkDetails,
    string? ExclusionReason,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? RotAmount,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? OtherCompensationAmount,
    bool? AdjustmentsAlreadyInAmount,
    bool Confirm);

public sealed record ReceiptExpenseResponse(
    Guid Id, string Description, DateOnly? ExpenseDate, ImprovementType Type, string? Amount,
    string? Currency, string? Notes, string? WorkDetails, string? ExclusionReason,
    string? RotAmount, string? OtherCompensationAmount, bool? AdjustmentsAlreadyInAmount,
    DateTimeOffset? ConfirmedAtUtc, int? ConfirmedReceiptVersion);

public sealed record DraftExpenseWarning(string Code, string Message);

public sealed record DraftExpenseResponse(
    Guid Id,
    Guid PropertyId,
    Guid ReceiptId,
    string Description,
    DateOnly? ExpenseDate,
    ImprovementType Type,
    string? Amount,
    string? Currency,
    string? Notes,
    DateTimeOffset UpdatedAtUtc,
    ReceiptResponse Source,
    IReadOnlyList<DraftExpenseWarning> Warnings)
{
    public ReceiptExpenseResponse? ReviewFacts { get; init; }
}

public sealed record ImprovementReadinessResponse(
    int ConfirmedExpenses, int PendingExpenses, int ExcludedExpenses,
    IReadOnlyList<string> MissingFacts, bool CanGenerateReport);
