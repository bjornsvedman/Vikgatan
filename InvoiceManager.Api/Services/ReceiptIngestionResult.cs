using InvoiceManager.Api.Contracts;

namespace InvoiceManager.Api.Services;

public sealed record ReceiptIngestionResult(
    ReceiptIngestionStatus Status,
    ReceiptResponse? Receipt);