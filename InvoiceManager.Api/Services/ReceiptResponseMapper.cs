using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Documents;

namespace InvoiceManager.Api.Services;

internal static class ReceiptResponseMapper
{
    public static ReceiptResponse ToResponse(Receipt receipt) => new(
        receipt.Id,
        receipt.FileName,
        receipt.DocumentDate,
        receipt.Vendor,
        receipt.Currency,
        receipt.TotalAmount,
        receipt.Category,
        receipt.Description,
        receipt.ProcessingStatus,
        receipt.ProcessingError,
        receipt.CreatedAtUtc,
        receipt.UpdatedAtUtc,
        receipt.LineItems
            .Select(item => new ReceiptLineItemResponse(
                item.Id,
                item.Description,
                item.Quantity,
                item.UnitPrice,
                item.LineTotal,
                item.Category))
            .ToArray());
}