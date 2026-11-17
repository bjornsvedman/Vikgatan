using System.Text.RegularExpressions;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptReviewService(InvoiceDbContext dbContext) : IReceiptReviewService
{
    private const int MaximumLineItems = 100;
    private static readonly Regex CurrencyCodePattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ReceiptResponse>> GetReviewQueueAsync(
        CancellationToken cancellationToken,
        Guid propertyId)
    {
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty propertyId is required.", nameof(propertyId));
        }
        var receipts = await dbContext.Receipts
            .AsNoTracking()
            .Include(receipt => receipt.LineItems).ThenInclude(item => item.Project)
            .Include(receipt => receipt.ImprovementExpenses)
            .Where(receipt => receipt.ProcessingStatus == ReceiptProcessingStatus.Pending
                || receipt.ProcessingStatus == ReceiptProcessingStatus.NeedsReview
                || receipt.ProcessingStatus == ReceiptProcessingStatus.Failed)
            .Where(receipt => receipt.PropertyId == propertyId)
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
            .Include(item => item.LineItems).ThenInclude(item => item.Project)
            .Include(item => item.ImprovementExpenses)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (receipt is null)
        {
            return new(ReceiptReviewStatus.NotFound, null, []);
        }

        request = request with { Currency = string.IsNullOrWhiteSpace(request.Currency)
            ? string.IsNullOrWhiteSpace(receipt.Currency) ? "SEK" : receipt.Currency : request.Currency };
        if (request.MarkReviewed && request.Expenses is not null)
            request = request with
            {
                Expenses = request.Expenses.Select(input => input is null ? input! : input with
                {
                    Description = string.IsNullOrWhiteSpace(input.Description)
                        ? receipt.Description ?? receipt.Vendor ?? receipt.FileName : input.Description,
                    ExpenseDate = input.ExpenseDate ?? request.DocumentDate,
                    Amount = request.TotalAmount,
                    Currency = request.Currency,
                    Confirm = true
                }).ToList()
            };
        if (request.Expenses is not null)
            request = request with { Expenses = request.Expenses.Select(input => input is null ? input! : input with
            {
                Currency = string.IsNullOrWhiteSpace(input.Currency)
                    ? receipt.ImprovementExpenses.SingleOrDefault(expense => expense.Id == input.Id)?.Currency ?? request.Currency : input.Currency,
                RotAmount = input.RotAmount ?? receipt.ImprovementExpenses.SingleOrDefault(expense => expense.Id == input.Id)?.RotAmount,
                OtherCompensationAmount = input.OtherCompensationAmount ?? receipt.ImprovementExpenses.SingleOrDefault(expense => expense.Id == input.Id)?.OtherCompensationAmount,
                AdjustmentsAlreadyInAmount = input.AdjustmentsAlreadyInAmount ?? receipt.ImprovementExpenses.SingleOrDefault(expense => expense.Id == input.Id)?.AdjustmentsAlreadyInAmount
            }).ToList() };
        var errors = Validate(request);
        var items = (request.LineItems ?? []).Where(item => item is not null).ToList();
        if (items.Where(item => item.Id.HasValue).GroupBy(item => item.Id).Any(group => group.Count() > 1)
            || items.Any(item => item.Id.HasValue && !receipt.LineItems.Any(stored => stored.Id == item.Id)))
            errors.Add("Line item IDs must be unique and belong to this receipt.");
        if (receipt.LineItems.Any(item => item.ProjectId != null) && items.Any(item => item.Id == null)
            && !receipt.LineItems.All(stored => items.Any(item => item.Id == stored.Id)))
            errors.Add("Reload the receipt and send stable line item IDs; assigned items cannot be replaced by an older client.");
        if ((items.Any(item => item.ProjectAssignmentSupplied) || receipt.LineItems.Any(item => item.ProjectId != null))
            && request.ReviewVersion is null)
            errors.Add("A review version is required to save project assignments.");
        var effectiveProjectIds = items.Select(item => item.ProjectAssignmentSupplied
            ? item.ProjectId
            : item.Id is Guid itemId ? receipt.LineItems.SingleOrDefault(stored => stored.Id == itemId)?.ProjectId : null)
            .Distinct()
            .ToArray();
        if (effectiveProjectIds.Length > 1)
            errors.Add("All line items on a receipt must belong to the same project.");
        var projectIds = items.Where(item => item.ProjectId != null).Select(item => item.ProjectId!.Value).Distinct().ToArray();
        var projects = await dbContext.Projects.Where(project => project.PropertyId == receipt.PropertyId && projectIds.Contains(project.Id))
            .ToDictionaryAsync(project => project.Id, cancellationToken);
        if (projectIds.Any(id => !projects.ContainsKey(id))) errors.Add("Projects must belong to this receipt's property.");
        if (request.MarkReviewed && request.Currency?.Trim().ToUpperInvariant() != "SEK")
            errors.Add("This version supports SEK approval only; no currency conversion is available.");
        if (request.Expenses is not null)
        {
            errors.AddRange(ReceiptExpenseReviewRules.Validate(request.Expenses,
                request.MarkReviewed, request.TotalAmount, request.Currency));
            if (request.ReviewVersion is null)
                errors.Add("A review version is required when saving expense portions.");
            if (request.Expenses.Any(expense => expense is not null && expense.Id.HasValue
                && !receipt.ImprovementExpenses.Any(stored => stored.Id == expense.Id)))
                errors.Add("Expense portion IDs must belong to this receipt.");
        }
        else if (request.MarkReviewed)
            errors.Add("Add one expense classification for the entire receipt before approval.");
        if (errors.Count > 0)
        {
            return new(ReceiptReviewStatus.Invalid, null, errors);
        }
        if (request.ReviewVersion is int version && version != receipt.ReviewVersion)
            return new(ReceiptReviewStatus.Conflict, null, ["The receipt changed. Reload it before saving; your changes have not been saved."]);

        var contentChanged = receipt.Vendor != NormalizeOptionalText(request.Vendor)
            || receipt.DocumentDate != request.DocumentDate
            || receipt.Currency != request.Currency?.Trim().ToUpperInvariant()
            || receipt.TotalAmount != request.TotalAmount
            || receipt.Description != NormalizeOptionalText(request.Description)
            || !receipt.LineItems.Select(item => new UpdateReceiptLineItemRequest
            {
                Description = item.Description,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.LineTotal
            }).OrderBy(item => item.Description).ThenBy(item => item.Quantity).ThenBy(item => item.UnitPrice)
                .ThenBy(item => item.LineTotal)
            .SequenceEqual(items.Select(item => new UpdateReceiptLineItemRequest
            {
                Description = item.Description!.Trim(), Quantity = item.Quantity, UnitPrice = item.UnitPrice,
                LineTotal = item.LineTotal
            }).OrderBy(item => item.Description).ThenBy(item => item.Quantity).ThenBy(item => item.UnitPrice)
                .ThenBy(item => item.LineTotal));

        var previousVersion = receipt.ReviewVersion++;
        if (contentChanged) ReceiptExpenseReviewRules.Invalidate(receipt);
        if (request.Expenses is not null)
        {
            var retainedIds = request.Expenses.Where(expense => expense.Id.HasValue).Select(expense => expense.Id!.Value).ToHashSet();
            foreach (var removed in receipt.ImprovementExpenses.Where(expense => !retainedIds.Contains(expense.Id)).ToArray())
            {
                dbContext.ImprovementExpenses.Remove(removed);
                receipt.ImprovementExpenses.Remove(removed);
            }
            foreach (var input in request.Expenses)
            {
                var expense = input.Id is Guid expenseId
                    ? receipt.ImprovementExpenses.Single(item => item.Id == expenseId)
                    : new ImprovementExpense { ReceiptId = receipt.Id, PropertyId = receipt.PropertyId };
                var changed = expense.Description != input.Description!.Trim()
                    || expense.ExpenseDate != input.ExpenseDate || expense.Type != input.Type
                    || expense.Amount != input.Amount || expense.Currency != input.Currency?.Trim().ToUpperInvariant()
                    || expense.Notes != NormalizeOptionalText(input.Notes)
                    || expense.WorkDetails != NormalizeOptionalText(input.WorkDetails)
                    || expense.ExclusionReason != NormalizeOptionalText(input.ExclusionReason)
                    || expense.RotAmount != input.RotAmount || expense.OtherCompensationAmount != input.OtherCompensationAmount
                    || expense.AdjustmentsAlreadyInAmount != input.AdjustmentsAlreadyInAmount;
                expense.Description = input.Description!.Trim();
                expense.ExpenseDate = input.ExpenseDate;
                expense.Type = input.Type;
                expense.Amount = input.Amount;
                expense.Currency = NormalizeOptionalText(input.Currency)?.ToUpperInvariant();
                expense.Notes = NormalizeOptionalText(input.Notes);
                expense.WorkDetails = NormalizeOptionalText(input.WorkDetails);
                expense.ExclusionReason = NormalizeOptionalText(input.ExclusionReason);
                expense.RotAmount = input.RotAmount;
                expense.OtherCompensationAmount = input.OtherCompensationAmount;
                expense.AdjustmentsAlreadyInAmount = input.AdjustmentsAlreadyInAmount;
                if (changed || !input.Confirm)
                {
                    expense.ConfirmedAtUtc = null;
                    expense.ConfirmedReceiptVersion = null;
                    expense.UpdatedAtUtc = DateTimeOffset.UtcNow;
                }
                if (request.MarkReviewed && input.Confirm)
                {
                    expense.ConfirmedAtUtc ??= DateTimeOffset.UtcNow;
                    expense.ConfirmedReceiptVersion = receipt.ReviewVersion;
                }
                if (input.Id is null)
                {
                    receipt.ImprovementExpenses.Add(expense);
                    dbContext.ImprovementExpenses.Add(expense);
                }
            }
        }
        foreach (var expense in receipt.ImprovementExpenses.Where(expense => expense.ConfirmedReceiptVersion == previousVersion))
            expense.ConfirmedReceiptVersion = receipt.ReviewVersion;

        receipt.Vendor = NormalizeOptionalText(request.Vendor);
        receipt.DocumentDate = request.DocumentDate;
        receipt.Currency = request.Currency?.Trim().ToUpperInvariant();
        receipt.TotalAmount = request.TotalAmount;
        receipt.Description = NormalizeOptionalText(request.Description);
        receipt.ProcessingStatus = request.MarkReviewed
            ? ReceiptProcessingStatus.Completed
            : receipt.ProcessingStatus == ReceiptProcessingStatus.Completed
                ? ReceiptProcessingStatus.Completed : ReceiptProcessingStatus.NeedsReview;
        receipt.ProcessingError = null;
        receipt.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (contentChanged)
        {
            receipt.Embedding = null;
            receipt.EmbeddingModel = null;
            receipt.EmbeddingModelVersion = null;
        }
        var keptIds = items.Where(item => item.Id != null).Select(item => item.Id!.Value).ToHashSet();
        foreach (var removed in receipt.LineItems.Where(item => !keptIds.Contains(item.Id)).ToArray())
        {
            dbContext.ReceiptLineItems.Remove(removed);
            receipt.LineItems.Remove(removed);
        }
        foreach (var item in items)
        {
            var stored = item.Id is Guid itemId ? receipt.LineItems.Single(line => line.Id == itemId)
                : new ReceiptLineItem { ReceiptId = receipt.Id, PropertyId = receipt.PropertyId };
            stored.Description = item.Description!.Trim();
            stored.Quantity = item.Quantity;
            stored.UnitPrice = item.UnitPrice;
            stored.LineTotal = item.LineTotal;
            if (item.ProjectAssignmentSupplied)
            {
                stored.ProjectId = item.ProjectId;
                stored.Project = item.ProjectId is Guid projectId ? projects[projectId] : null;
            }
            if (item.Id is null)
            {
                receipt.LineItems.Add(stored);
                dbContext.ReceiptLineItems.Add(stored);
            }
        }

        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return new(ReceiptReviewStatus.Conflict, null, ["The receipt changed while saving. Reload it before retrying."]);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.ForeignKeyViolation,
                ConstraintName: "FK_ImprovementExpenses_Receipts_PropertyId_ReceiptId"
            })
        {
            return new(ReceiptReviewStatus.Conflict, null, ["The source receipt was removed while saving. No review changes were saved."]);
        }
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
        if (request.TotalAmount is decimal total &&
            (total >= 100000000000000m || decimal.Round(total, 4) != total))
            errors.Add("Total amount must be below 100000000000000 and have at most four decimal places.");

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
            if (item is null)
            {
                errors.Add($"Line item {index + 1} cannot be null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Trim().Length > 1000)
            {
                errors.Add($"Line item {index + 1} needs a description of 1 to 1000 characters.");
            }

            if (item.Quantity < 0 || item.UnitPrice < 0 || item.LineTotal < 0)
            {
                errors.Add($"Line item {index + 1} values cannot be negative.");
            }
            if (new[] { item.Quantity, item.UnitPrice, item.LineTotal }.Any(value =>
                value is decimal amount && (amount >= 100000000000000m || decimal.Round(amount, 4) != amount)))
                errors.Add($"Line item {index + 1} values must be below 100000000000000 and have at most four decimal places.");

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