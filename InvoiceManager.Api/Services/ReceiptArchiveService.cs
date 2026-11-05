using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptArchiveService(InvoiceDbContext dbContext)
{
    public async Task<ReceiptArchiveResponse> BrowseAsync(
        ReceiptArchiveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PropertyId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty propertyId is required.", nameof(request));
        }

        if (request.Page is < 1 or > 1000000 || request.PageSize is < 1 or > 100
            || request.Year is < 1 or > 9999
            || request.SortBy is not ("date" or "category" or "vendor" or "amount")
            || request.Direction is not ("asc" or "desc")
            || request.DateFrom > request.DateTo
            || (request.Undated && (request.Year != null || request.DateFrom != null || request.DateTo != null))
            || (request.Uncategorized && !string.IsNullOrWhiteSpace(request.Category))
            || request.Category?.Length > 128 || request.Vendor?.Length > 256 || request.Currency?.Length > 3)
        {
            throw new ArgumentException("Archive filters, sort order, date range, or page are invalid.");
        }

        var all = dbContext.Receipts.AsNoTracking()
            .Where(receipt => receipt.PropertyId == request.PropertyId);
        var query = all;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<ReceiptProcessingStatus>(request.Status, out var status)
                || !Enum.IsDefined(status))
            {
                throw new ArgumentException("The receipt status is invalid.");
            }
            query = query.Where(receipt => receipt.ProcessingStatus == status);
        }
        if (request.Year is int year)
            query = query.Where(receipt => receipt.DocumentDate != null && receipt.DocumentDate.Value.Year == year);
        if (request.Undated)
            query = query.Where(receipt => receipt.DocumentDate == null);
        if (request.DateFrom is DateOnly dateFrom)
            query = query.Where(receipt => receipt.DocumentDate >= dateFrom);
        if (request.DateTo is DateOnly dateTo)
            query = query.Where(receipt => receipt.DocumentDate <= dateTo);
        if (request.Uncategorized)
            query = query.Where(receipt => receipt.Category == null || receipt.Category == "");
        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var category = request.Category.Trim().ToLowerInvariant();
            query = query.Where(receipt => receipt.Category != null && receipt.Category.ToLower() == category);
        }
        if (!string.IsNullOrWhiteSpace(request.Vendor))
        {
            var vendor = request.Vendor.Trim().ToLowerInvariant();
            query = query.Where(receipt => receipt.Vendor != null && receipt.Vendor.ToLower() == vendor);
        }
        if (!string.IsNullOrWhiteSpace(request.Currency))
        {
            var currency = request.Currency.Trim().ToUpperInvariant();
            query = query.Where(receipt => receipt.Currency == currency);
        }

        var ordered = query.OrderBy(receipt => receipt.DocumentDate == null)
            .ThenByDescending(receipt => receipt.DocumentDate.HasValue ? receipt.DocumentDate.Value.Year : 0);
        var descending = request.Direction == "desc";
        ordered = request.SortBy switch
        {
            "category" => descending ? ordered.ThenByDescending(receipt => receipt.Category) : ordered.ThenBy(receipt => receipt.Category),
            "vendor" => descending ? ordered.ThenByDescending(receipt => receipt.Vendor) : ordered.ThenBy(receipt => receipt.Vendor),
            "amount" => descending ? ordered.ThenByDescending(receipt => receipt.TotalAmount) : ordered.ThenBy(receipt => receipt.TotalAmount),
            _ => descending ? ordered.ThenByDescending(receipt => receipt.DocumentDate) : ordered.ThenBy(receipt => receipt.DocumentDate)
        };

        var count = await query.CountAsync(cancellationToken);
        var receipts = await ordered.ThenBy(receipt => receipt.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Include(receipt => receipt.LineItems)
            .ToListAsync(cancellationToken);
        var years = await all.Where(receipt => receipt.DocumentDate != null)
            .Select(receipt => receipt.DocumentDate!.Value.Year).Distinct().OrderByDescending(year => year)
            .ToListAsync(cancellationToken);
        var categories = await all.Where(receipt => receipt.Category != null && receipt.Category != "")
            .Select(receipt => receipt.Category!).Distinct().OrderBy(category => category).ToListAsync(cancellationToken);
        var vendors = await all.Where(receipt => receipt.Vendor != null && receipt.Vendor != "")
            .Select(receipt => receipt.Vendor!).Distinct().OrderBy(vendor => vendor).ToListAsync(cancellationToken);
        var currencies = await all.Where(receipt => receipt.Currency != null && receipt.Currency != "")
            .Select(receipt => receipt.Currency!).Distinct().OrderBy(currency => currency).ToListAsync(cancellationToken);
        return new(receipts.Select(ReceiptResponseMapper.ToResponse).ToArray(), count,
            request.Page, request.PageSize, years, categories, vendors, currencies);
    }
}