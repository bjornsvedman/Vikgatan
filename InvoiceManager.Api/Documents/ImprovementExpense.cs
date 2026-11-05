namespace InvoiceManager.Api.Documents;

public sealed class ImprovementExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public Guid ReceiptId { get; set; }
    public Receipt Receipt { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public DateOnly? ExpenseDate { get; set; }
    public ImprovementType Type { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Notes { get; set; }
    public string? WorkDetails { get; set; }
    public string? ExclusionReason { get; set; }
    public decimal? RotAmount { get; set; }
    public decimal? OtherCompensationAmount { get; set; }
    public bool? AdjustmentsAlreadyInAmount { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public int? ConfirmedReceiptVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum ImprovementType
{
    Unclassified,
    BasicImprovement,
    RepairsAndMaintenance,
    Excluded
}
