namespace InvoiceManager.Api.Services;

public enum ReceiptIngestionStatus
{
    Created,
    Duplicate,
    EmptyFile,
    FileTooLarge,
    UnsupportedFileType,
    InvalidFileContents
}