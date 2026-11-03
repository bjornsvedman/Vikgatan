namespace InvoiceManager.Api.Documents;

public enum ReceiptProcessingStatus
{
    Pending,
    Processing,
    NeedsReview,
    Completed,
    Failed
}