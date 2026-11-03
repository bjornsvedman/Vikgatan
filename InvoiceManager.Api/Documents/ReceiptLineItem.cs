namespace InvoiceManager.Api.Documents;

public sealed class ReceiptLineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReceiptId { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }

    public decimal? UnitPrice { get; set; }

    public decimal? LineTotal { get; set; }

    public string? Category { get; set; }

    public Receipt Receipt { get; set; } = null!;
}