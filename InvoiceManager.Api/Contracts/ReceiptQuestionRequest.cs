namespace InvoiceManager.Api.Contracts;

/// <summary>Contains a natural-language question about stored receipts.</summary>
public sealed record ReceiptQuestionRequest
{
    public required string Question { get; init; }
    public Guid? ProjectId { get; init; }
    public bool UnassignedProject { get; init; }
}