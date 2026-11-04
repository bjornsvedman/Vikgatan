namespace InvoiceManager.Api.Services;

public interface IReceiptQueryPlanner
{
    Task<ReceiptQueryPlan> PlanAsync(string question, CancellationToken cancellationToken);
}