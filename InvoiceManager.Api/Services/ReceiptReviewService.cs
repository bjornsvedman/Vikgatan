using System.Text.RegularExpressions;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptReviewService(InvoiceDbContext dbContext) : IReceiptReviewService
{
    private const int MaximumLineItems = 100;
    private static readonly Regex CurrencyCodePattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ReceiptResponse>> GetReviewQueueAsync(
        CancellationToken cancellationToken)
    {
        var receipts = await dbContext.Receipts
            .AsNoTracking()
            .Include(receipt => receipt.LineItems)
            .Where(receipt => receipt.ProcessingStatus == ReceiptProcessingStatus.Pending
                || receipt.ProcessingStatus == ReceiptProcessingStatus.NeedsReview)
            .OrderBy(receipt => receipt.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return receipts.Select(ReceiptResponseMapper.ToResponse).ToArray();
    }

    public async Task<ReceiptReviewResult> ReviewAsync(
        Guid id,
        UpdateReceiptReviewRequest request,
        CancellationToken cancellationToken)
    {
        var receipt = await dbContext.Receipts
            .Include(item => item.LineItems)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (receipt is null)
        {
            return new(ReceiptReviewStatus.NotFound, null, []);
        }

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return new(ReceiptReviewStatus.Invalid, null, errors);
        }

        receipt.Vendor = NormalizeOptionalText(request.Vendor);
        receipt.DocumentDate = request.DocumentDate;
        receipt.Currency = request.Currency?.Trim().ToUpperInvariant();
        receipt.TotalAmount = request.TotalAmount;
        receipt.Category = NormalizeOptionalText(request.Category);
        receipt.Description = NormalizeOptionalText(request.Description);
        receipt.ProcessingStatus = request.MarkReviewed
            ? ReceiptProcessingStatus.Completed
            : ReceiptProcessingStatus.NeedsReview;
        receipt.ProcessingError = null;
        receipt.UpdatedAtUtc = DateTimeOffset.UtcNow;
        receipt.LineItems.Clear();

        foreach (var item in request.LineItems ?? [])
        {
            dbContext.ReceiptLineItems.Add(new ReceiptLineItem
            {
                ReceiptId = receipt.Id,
                Description = item.Description!.Trim(),
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.LineTotal,
                Category = NormalizeOptionalText(item.Category)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ReceiptReviewStatus.Updated, ReceiptResponseMapper.ToResponse(receipt), []);
    }

    private static List<string> Validate(UpdateReceiptReviewRequest request)
    {
        var errors = new List<string>();
        var lineItems = request.LineItems ?? [];

        if (request.Vendor?.Length > 256)
        {
            errors.Add("Vendor must be 256 characters or fewer.");
        }

        if (request.Currency is not null && !CurrencyCodePattern.IsMatch(request.Currency.Trim()))
        {
            errors.Add("Currency must be a three-letter code.");
        }

        if (request.TotalAmount < 0)
        {
            errors.Add("Total amount cannot be negative.");
        }

        if (request.Category?.Length > 128)
        {
            errors.Add("Category must be 128 characters or fewer.");
        }

        if (request.Description?.Length > 4000)
        {
            errors.Add("Description must be 4000 characters or fewer.");
        }

        if (lineItems.Count > MaximumLineItems)
        {
            errors.Add($"A receipt can contain at most {MaximumLineItems} line items.");
        }

        for (var index = 0; index < lineItems.Count; index++)
        {
            var item = lineItems[index];
            if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Trim().Length > 1000)
            {
                errors.Add($"Line item {index + 1} needs a description of 1 to 1000 characters.");
            }

            if (item.Quantity < 0 || item.UnitPrice < 0 || item.LineTotal < 0)
            {
                errors.Add($"Line item {index + 1} values cannot be negative.");
            }

            if (item.Category?.Length > 128)
            {
                errors.Add($"Line item {index + 1} category must be 128 characters or fewer.");
            }
        }

        if (request.MarkReviewed)
        {
            if (string.IsNullOrWhiteSpace(request.Vendor))
            {
                errors.Add("Vendor is required to complete review.");
            }

            if (request.DocumentDate is null)
            {
                errors.Add("Document date is required to complete review.");
            }

            if (request.Currency is null)
            {
                errors.Add("Currency is required to complete review.");
            }

            if (request.TotalAmount is null)
            {
                errors.Add("Total amount is required to complete review.");
            }
        }

        return errors;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}