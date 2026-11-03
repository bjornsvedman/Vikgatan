namespace InvoiceManager.Api.Services;

public sealed class FoundryExtractionOptions
{
    public const string SectionName = "Foundry:Extraction";
    public const string DefaultTokenScope = "https://ai.azure.com/.default";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public string TokenScope { get; set; } = DefaultTokenScope;
}