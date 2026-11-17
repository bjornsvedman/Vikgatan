using System.Text.Json.Serialization;

namespace InvoiceManager.Api.Contracts;

/// <summary>Contains user-verified metadata and line items for a receipt.</summary>
public sealed record UpdateReceiptReviewRequest
{
    public string? Vendor { get; init; }

    public DateOnly? DocumentDate { get; init; }

    public string? Currency { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? TotalAmount { get; init; }

    public string? Description { get; init; }

    public List<UpdateReceiptLineItemRequest>? LineItems { get; init; } = [];

    public bool MarkReviewed { get; init; }

    public int? ReviewVersion { get; init; }

    public List<ReceiptExpenseRequest>? Expenses { get; init; }
}

/// <summary>Contains a user-verified invoice line item.</summary>
public sealed record UpdateReceiptLineItemRequest
{
    public Guid? Id { get; init; }
    private Guid? _projectId;
    public Guid? ProjectId { get => _projectId; init { _projectId = value; ProjectAssignmentSupplied = true; } }
    [JsonIgnore]
    public bool ProjectAssignmentSupplied { get; private init; }
    public string? Description { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? Quantity { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? UnitPrice { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? LineTotal { get; init; }
}