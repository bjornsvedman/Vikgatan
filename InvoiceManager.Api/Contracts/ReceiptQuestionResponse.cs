using InvoiceManager.Api.Documents;

namespace InvoiceManager.Api.Contracts;

/// <summary>Contains an answer and the receipts used to support it.</summary>
public sealed record ReceiptQuestionResponse(
    string Answer,
    IReadOnlyList<ReceiptQuestionCitation> Sources,
    int? TotalSourceCount = null);

/// <summary>Identifies a receipt cited by an answer.</summary>
public sealed record ReceiptQuestionCitation(
    Guid Id,
    string FileName,
    string? Vendor,
    DateOnly? DocumentDate,
    string? Currency,
    decimal? TotalAmount,
    ReceiptProcessingStatus Status);