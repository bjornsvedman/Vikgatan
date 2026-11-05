using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class ImprovementService(InvoiceDbContext dbContext)
{
    public async Task<SaleFactsRequest?> GetSaleFactsAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await dbContext.Properties.AsNoTracking().Where(property => property.Id == propertyId)
            .Select(property => new SaleFactsRequest(property.PropertyType, property.AcquisitionDate, property.SaleDate))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SaleFactsRequest?> SaveSaleFactsAsync(
        Guid propertyId, SaleFactsRequest request, CancellationToken cancellationToken)
    {
        if (request.PropertyType is { } type && !Enum.IsDefined(type))
            throw new ArgumentException("Select a supported property type.");
        if (request.AcquisitionDate > request.SaleDate)
            throw new ArgumentException("Sale date cannot precede acquisition date.");
        var property = await dbContext.Properties.SingleOrDefaultAsync(item => item.Id == propertyId, cancellationToken);
        if (property is null) return null;
        property.PropertyType = request.PropertyType;
        property.AcquisitionDate = request.AcquisitionDate;
        property.SaleDate = request.SaleDate;
        await dbContext.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<IReadOnlyList<DraftExpenseResponse>?> ListAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Properties.AnyAsync(property => property.Id == propertyId, cancellationToken)) return null;
        var expenses = await dbContext.ImprovementExpenses.AsNoTracking()
            .Where(expense => expense.PropertyId == propertyId)
            .Include(expense => expense.Receipt).ThenInclude(receipt => receipt.LineItems)
            .OrderByDescending(expense => expense.CreatedAtUtc).ThenBy(expense => expense.Id)
            .ToListAsync(cancellationToken);
        return expenses.Select(expense => ToResponse(expense, expenses)).ToArray();
    }

    public async Task<DraftExpenseResponse?> SaveAsync(
        Guid propertyId, Guid? id, DraftExpenseRequest request, CancellationToken cancellationToken)
    {
        if (!await dbContext.Properties.AnyAsync(property => property.Id == propertyId, cancellationToken)) return null;
        var expense = id is Guid expenseId
            ? await dbContext.ImprovementExpenses.SingleOrDefaultAsync(
                item => item.Id == expenseId && item.PropertyId == propertyId, cancellationToken)
            : new ImprovementExpense { PropertyId = propertyId };
        if (expense is null) return null;
        ValidateDraft(request);
        var receipt = await dbContext.Receipts.Include(item => item.LineItems).Include(item => item.ImprovementExpenses).SingleOrDefaultAsync(
            item => item.Id == request.ReceiptId && item.PropertyId == propertyId, cancellationToken);
        if (receipt is null) throw new ArgumentException("Select a receipt belonging to this property.");
        if (receipt.ProcessingStatus != ReceiptProcessingStatus.Completed)
            throw new ArgumentException("Only reviewed receipts may support draft expenses.");
        if (id is not null && expense.ReceiptId != receipt.Id)
            throw new ArgumentException("An existing expense cannot be moved to another receipt. Remove it and add the expense portion through receipt review.");
        ReceiptExpenseReviewRules.Invalidate(receipt);
        receipt.ReviewVersion++;
        expense.ReceiptId = receipt.Id;
        expense.Receipt = receipt;
        expense.Description = request.Description!.Trim();
        expense.ExpenseDate = request.ExpenseDate;
        expense.Type = request.Type;
        expense.Amount = request.Amount;
        expense.Currency = string.IsNullOrWhiteSpace(request.Currency) ? null : request.Currency.Trim().ToUpperInvariant();
        expense.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        expense.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (id is null) dbContext.ImprovementExpenses.Add(expense);
        await dbContext.SaveChangesAsync(cancellationToken);
        var siblings = await dbContext.ImprovementExpenses.AsNoTracking()
            .Where(item => item.PropertyId == propertyId && item.ReceiptId == receipt.Id)
            .ToListAsync(cancellationToken);
        return ToResponse(expense, siblings);
    }

    public async Task<bool> DeleteAsync(Guid propertyId, Guid expenseId, CancellationToken cancellationToken)
    {
        var expense = await dbContext.ImprovementExpenses.Include(item => item.Receipt)
            .ThenInclude(receipt => receipt.ImprovementExpenses).SingleOrDefaultAsync(
            item => item.Id == expenseId && item.PropertyId == propertyId, cancellationToken);
        if (expense is null) return false;
        ReceiptExpenseReviewRules.Invalidate(expense.Receipt);
        expense.Receipt.ReviewVersion++;
        dbContext.ImprovementExpenses.Remove(expense);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static void ValidateDraft(DraftExpenseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 1000)
            throw new ArgumentException("Description must contain 1 to 1000 characters.");
        if (request.ReceiptId == Guid.Empty) throw new ArgumentException("A receipt ID is required.");
        if (!Enum.IsDefined(request.Type)) throw new ArgumentException("Select a supported draft classification.");
        if (request.Amount is decimal amount &&
            (amount < 0 || (amount == 0 && request.Type != ImprovementType.Excluded)
                || amount >= 100000000000000m || decimal.Round(amount, 4) != amount))
            throw new ArgumentException("Amount must be positive (or zero for an excluded split), below 100000000000000, and have at most four decimal places.");
        if (!string.IsNullOrWhiteSpace(request.Currency) &&
            !Regex.IsMatch(request.Currency.Trim(), "^[A-Za-z]{3}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Currency must be a three-letter code or left blank.");
        if (request.Notes?.Length > 4000) throw new ArgumentException("Notes must not exceed 4000 characters.");
    }

    private static DraftExpenseResponse ToResponse(ImprovementExpense expense, IReadOnlyList<ImprovementExpense> all)
    {
        var source = expense.Receipt;
        var warnings = new List<DraftExpenseWarning>();
        if (expense.ConfirmedAtUtc is null || expense.ConfirmedReceiptVersion != source.ReviewVersion)
            warnings.Add(new("ClassificationPending", "Confirm this expense's classification and known facts in receipt review."));
        if (expense.Type == ImprovementType.RepairsAndMaintenance)
            warnings.Add(new("SaleAssessmentPending", "Sale-year eligibility and work-specific condition/wear assessment are still required; classification is not deductibility."));
        if (expense.Amount is null || expense.Currency is null || expense.ExpenseDate is null || expense.Type == ImprovementType.Unclassified)
            warnings.Add(new("IncompleteDraft", "Date, amount, currency, or provisional classification is missing. This remains an unapproved draft."));
        if (source.TotalAmount is null || string.IsNullOrWhiteSpace(source.Currency))
            warnings.Add(new("IncompleteSource", "The source receipt has no complete amount/currency; allocation cannot be checked."));
        if (source.ProcessingStatus != ReceiptProcessingStatus.Completed)
            warnings.Add(new("UnreviewedSource", "The source is no longer reviewed. Review it before editing this expense."));
        if (expense.Currency is not null && !string.IsNullOrWhiteSpace(source.Currency) && expense.Currency != source.Currency)
            warnings.Add(new("CurrencyMismatch", "Draft and source currencies differ. No conversion or allocation comparison is performed."));
        var siblings = all.Where(item => item.ReceiptId == expense.ReceiptId).ToArray();
        if (siblings.Any(item => item.Amount is null || item.Currency is null || item.Currency != source.Currency))
            warnings.Add(new("AllocationIncomplete", "Some expense portions have missing amounts or different currencies; no combined allocation is calculated."));
        else if (source.TotalAmount is decimal total && !string.IsNullOrWhiteSpace(source.Currency))
        {
            var allocated = siblings.Sum(item => item.Amount!.Value);
            if (allocated > total)
                warnings.Add(new("OverAllocated",
                    $"Draft expense portions allocate {allocated.ToString("0.00##", CultureInfo.InvariantCulture)} {source.Currency}, above the source total {total.ToString("0.00##", CultureInfo.InvariantCulture)}. Check for overlap; saving is allowed."));
            else if (allocated < total)
                warnings.Add(new("UnderAllocated", "Expense portions do not cover the source total. Classify the remaining portion, including excluded items, before confirmation."));
        }
        return new(expense.Id, expense.PropertyId, expense.ReceiptId, expense.Description, expense.ExpenseDate,
            expense.Type, expense.Amount?.ToString("0.00##", CultureInfo.InvariantCulture), expense.Currency,
            expense.Notes, expense.UpdatedAtUtc, ReceiptResponseMapper.ToResponse(source), warnings)
        { ReviewFacts = ReceiptResponseMapper.ToExpenseResponse(expense) };
    }

    public async Task<ImprovementReadinessResponse?> GetReadinessAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var facts = await GetSaleFactsAsync(propertyId, cancellationToken);
        if (facts is null) return null;
        var receipts = await dbContext.Receipts.AsNoTracking().Include(receipt => receipt.ImprovementExpenses)
            .Where(receipt => receipt.PropertyId == propertyId).ToListAsync(cancellationToken);
        var expenses = receipts.SelectMany(receipt => receipt.ImprovementExpenses).ToArray();
        var confirmed = expenses.Count(expense => expense.ConfirmedAtUtc is not null
            && expense.ConfirmedReceiptVersion == expense.Receipt.ReviewVersion
            && expense.Receipt.ProcessingStatus == ReceiptProcessingStatus.Completed);
        var missing = new List<string>();
        if (facts.PropertyType is null) missing.Add("Property type");
        if (facts.AcquisitionDate is null) missing.Add("Acquisition date");
        if (facts.SaleDate is null) missing.Add("Sale date (when known)");
        if (receipts.Any(receipt => receipt.ImprovementExpenses.Count == 0))
            missing.Add("Receipt expense classifications, including explicit excluded receipts");
        if (confirmed != expenses.Length) missing.Add("Expense classification confirmation / reviewed sources");
        if (receipts.Any(receipt => receipt.TotalAmount is null || receipt.Currency is null
            || receipt.ImprovementExpenses.Any(expense => expense.Amount is null || expense.Currency != receipt.Currency)
            || receipt.ImprovementExpenses.Sum(expense => expense.Amount ?? 0) != receipt.TotalAmount))
            missing.Add("Complete receipt amount coverage in one currency (included and excluded expense portions)");
        if (expenses.Any(expense => expense.Type == ImprovementType.RepairsAndMaintenance))
            missing.Add("Work-specific sale condition/wear and latest-repair assessments (not yet implemented)");
        if (expenses.Any(expense => expense.Currency != "SEK"))
            missing.Add("Verified currency handling for non-SEK expenses (not yet implemented)");
        missing.Add("Verified eligibility calculations and worksheet export (not yet implemented)");
        return new(confirmed, expenses.Length - confirmed,
            expenses.Count(expense => expense.Type == ImprovementType.Excluded), missing, false);
    }
}
