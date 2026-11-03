namespace InvoiceManager.Api.Services;

public interface IEmbeddingProvider
{
    string ModelName { get; }

    int Dimensions { get; }

    Task<float[]> GenerateAsync(string input, CancellationToken cancellationToken);
}