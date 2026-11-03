namespace InvoiceManager.Api.Services;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";
    public const long MaximumFileBytes = 20 * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumFileBytes + 64 * 1024;

    public string RootPath { get; set; } = "storage/documents";
}