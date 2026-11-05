using InvoiceManager.Api.Contracts;

namespace InvoiceManager.Api.Services;

public interface IReceiptQuestionService
{
    Task<ReceiptQuestionResponse> QueryAsync(
        string question,
        CancellationToken cancellationToken,
        Guid propertyId);
}