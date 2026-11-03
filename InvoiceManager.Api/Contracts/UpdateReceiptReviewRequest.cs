namespace InvoiceManager.Api.Contracts;

/// <summary>Contains user-verified metadata and line items for a receipt.</summary>
public sealed record UpdateReceiptReviewRequest
{
    public string? Vendor { get; init; }

    public DateOnly? DocumentDate { get; init; }

    public string? Currency { get; init; }

    public decimal? TotalAmount { get; init; }

    public string? Category { get; init; }

    public string? Description { get; init; }

    public List<UpdateReceiptLineItemRequest>? LineItems { get; init; } = [];

    public bool MarkReviewed { get; init; }
}

/// <summary>Contains a user-verified invoice line item.</summary>
public sealed record UpdateReceiptLineItemRequest
{
    public string? Description { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? UnitPrice { get; init; }

    public decimal? LineTotal { get; init; }

    public string? Category { get; init; }
}