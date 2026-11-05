namespace InvoiceManager.Api.Documents;

public sealed class Property
{
    public static readonly Guid DefaultPropertyId = new("7a4888e4-73d2-48f1-a9a8-07c55e2384b1");

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<Receipt> Receipts { get; set; } = [];
}
