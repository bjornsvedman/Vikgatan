namespace InvoiceManager.Api.Services;

public interface IReceiptExtractionProvider
{
    Task<ReceiptExtractionResult> ExtractAsync(
        Stream fileStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);
}