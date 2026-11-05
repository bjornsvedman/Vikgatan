using InvoiceManager.Api.Contracts;

namespace InvoiceManager.Api.Services;

public interface IReceiptReviewService
{
    Task<IReadOnlyList<ReceiptResponse>> GetReviewQueueAsync(
        CancellationToken cancellationToken,
        Guid propertyId);

    Task<ReceiptReviewResult> ReviewAsync(
        Guid id,
        UpdateReceiptReviewRequest request,
        CancellationToken cancellationToken);
}