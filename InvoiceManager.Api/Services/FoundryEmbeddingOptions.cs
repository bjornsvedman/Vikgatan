namespace InvoiceManager.Api.Services;

public sealed class FoundryEmbeddingOptions
{
    public const string SectionName = "Foundry:Embeddings";
    public const int DefaultDimensions = 1536;
    public const string DefaultTokenScope = "https://cognitiveservices.azure.com/.default";

    public string Endpoint { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public int Dimensions { get; set; } = DefaultDimensions;

    public string TokenScope { get; set; } = DefaultTokenScope;
}