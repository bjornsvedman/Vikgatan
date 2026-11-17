using System.Text.Json.Serialization;

namespace InvoiceManager.Api.Services;

public sealed record ReceiptAnswerDraft(
    string Answer,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<Guid> SourceIds);

public sealed record ReceiptQueryPlan(
    [property: JsonPropertyName("query_type")] string QueryType,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("project")] string? Project = null);

public sealed record ReceiptQuestionDocument(
    Guid Id,
    string FileName,
    string? Vendor,
    DateOnly? DocumentDate,
    string? Currency,
    decimal? TotalAmount,
    string? Description,
    string Status,
    IReadOnlyList<ReceiptQuestionLineItem> LineItems);

public sealed record ReceiptQuestionLineItem(
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? LineTotal);