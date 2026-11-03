using System.Text.Json.Serialization;

namespace InvoiceManager.Api.Services;

public sealed record ReceiptExtractionResult(ExtractedReceipt Metadata, string RawJson);

public sealed record ExtractedReceipt(
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("document_date")] string? DocumentDate,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("total_amount")] decimal? TotalAmount,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("line_items")] IReadOnlyList<ExtractedReceiptLineItem>? LineItems);

public sealed record ExtractedReceiptLineItem(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("quantity")] decimal? Quantity,
    [property: JsonPropertyName("unit_price")] decimal? UnitPrice,
    [property: JsonPropertyName("line_total")] decimal? LineTotal,
    [property: JsonPropertyName("category")] string? Category);