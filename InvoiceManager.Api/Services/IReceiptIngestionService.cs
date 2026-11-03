using InvoiceManager.Api.Contracts;
using Microsoft.AspNetCore.Http;

namespace InvoiceManager.Api.Services;

public interface IReceiptIngestionService
{
    Task<ReceiptIngestionResult> IngestAsync(IFormFile file, CancellationToken cancellationToken);

    Task<ReceiptResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}