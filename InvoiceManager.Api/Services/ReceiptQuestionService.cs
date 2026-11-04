using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptQuestionService(
    InvoiceDbContext dbContext,
    IEmbeddingProvider embeddingProvider,
    IReceiptQueryPlanner queryPlanner,
    IReceiptAnswerProvider answerProvider,
    ILogger<ReceiptQuestionService> logger) : IReceiptQuestionService
{
    private const int MaximumQuestionLength = 2000;
    private const int MaximumContextDocuments = 10;
    private const int MaximumSemanticMatches = 8;
    private const int MaximumLineItemsPerDocument = 20;
    private const int MaximumAggregateCitations = 20;
    private static readonly Regex ReceiptLookupPattern = new(
        @"^\s*(which|find|list|show(?: me)?)\s+(receipts?|invoices?|documents?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TotalQuestionPattern = new(
        @"\b(how much|total(?:s)?|sum|spent|spending|overall cost|combined cost|altogether)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnsupportedCalculationPattern = new(
        @"\b(average|median|percentage|percent|how many|count of|difference between|cheapest|most expensive)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnsafeSemanticAnswerPattern = new(
        @"[\p{N}\p{Sc}%+=]|\b(zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|billion|trillion|dozen|half|quarter|double|triple|grand|total(?:s|ed|ing)?|sum|amount|spent|spending|cost(?:s)?|average|median|percentage|percent|sek|eur|usd|gbp|dkk|nok|kr|kronor|dollars?|euros?|pounds?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<ReceiptQuestionResponse> QueryAsync(
        string question,
        CancellationToken cancellationToken)
    {
        var normalizedQuestion = question.Trim();
        if (normalizedQuestion.Length is 0 or > MaximumQuestionLength)
        {
            throw new ArgumentException(
                $"Question must contain 1 to {MaximumQuestionLength} characters.",
                nameof(question));
        }

        var hasReceipts = await dbContext.Receipts
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        if (!hasReceipts)
        {
            return new("There are no receipts available to answer this question yet.", []);
        }

        var plan = await queryPlanner.PlanAsync(normalizedQuestion, cancellationToken);
        if (!IsValidPlan(plan))
        {
            logger.LogWarning("Foundry returned an unsupported receipt query plan.");
            return new("I couldn't safely interpret that question. Try asking about a specific receipt or total.", []);
        }

        var isReceiptLookup = ReceiptLookupPattern.IsMatch(normalizedQuestion);
        if (!isReceiptLookup && UnsupportedCalculationPattern.IsMatch(normalizedQuestion))
        {
            return new("That calculation isn't supported yet. Ask for a spending total by year, vendor, or category.", []);
        }

        if (plan.QueryType == "sum_total" || (!isReceiptLookup && TotalQuestionPattern.IsMatch(normalizedQuestion)))
        {
            return await AnswerTotalAsync(plan with { QueryType = "sum_total" }, cancellationToken);
        }

        var documents = await FindContextAsync(normalizedQuestion, cancellationToken);
        if (documents.Count == 0)
        {
            return new("There are no receipts available to answer this question yet.", []);
        }

        var draft = await answerProvider.AnswerAsync(normalizedQuestion, documents, cancellationToken);
        var documentById = documents.ToDictionary(document => document.Id);
        var proposedSourceIds = draft.SourceIds ?? [];
        var sourceIds = proposedSourceIds
            .Distinct()
            .Where(documentById.ContainsKey)
            .ToHashSet();

        if (sourceIds.Count == 0 || string.IsNullOrWhiteSpace(draft.Answer))
        {
            logger.LogWarning("Foundry returned an answer without valid receipt citations.");
            return new("I couldn't verify an answer from the retrieved receipts.", []);
        }

        var sources = documents
            .Where(document => sourceIds.Contains(document.Id))
            .Select(document => new ReceiptQuestionCitation(
                document.Id,
                document.FileName,
                document.Vendor,
                document.DocumentDate,
                document.Currency,
                document.TotalAmount,
                Enum.Parse<ReceiptProcessingStatus>(document.Status)))
            .ToArray();

        var answer = UnsafeSemanticAnswerPattern.IsMatch(draft.Answer)
            ? "I found relevant receipts. Their stored details are available in the sources. Ask for a spending total for a database-calculated answer."
            : draft.Answer;
        return new(answer, sources, sources.Length);
    }

    private static bool IsValidPlan(ReceiptQueryPlan plan) =>
        plan.QueryType is "semantic" or "sum_total"
        && plan.GroupBy is "none" or "year" or "vendor" or "category"
        && (plan.Year is null or >= 1900 and <= 2100)
        && (plan.Vendor is null or { Length: <= 256 })
        && (plan.Category is null or { Length: <= 128 });

    private async Task<ReceiptQuestionResponse> AnswerTotalAsync(
        ReceiptQueryPlan plan,
        CancellationToken cancellationToken)
    {
        await using var snapshot = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        var query = dbContext.Receipts
            .AsNoTracking()
            .Where(receipt => receipt.ProcessingStatus == ReceiptProcessingStatus.Completed
                && receipt.TotalAmount != null);

        var vendor = plan.Vendor?.Trim();
        if (!string.IsNullOrWhiteSpace(vendor))
        {
            var normalizedVendor = vendor.ToLowerInvariant();
            query = query.Where(receipt => receipt.Vendor != null
                && receipt.Vendor.ToLower() == normalizedVendor);
        }

        var category = plan.Category?.Trim();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalizedCategory = category.ToLowerInvariant();
            query = query.Where(receipt => receipt.Category != null
                && receipt.Category.ToLower() == normalizedCategory);
        }

        if (plan.Year is int year)
        {
            query = query.Where(receipt => receipt.DocumentDate != null
                && receipt.DocumentDate.Value.Year == year);
        }

        var aggregates = plan.GroupBy switch
        {
            "year" => await query
                .GroupBy(receipt => new
                {
                    Year = receipt.DocumentDate.HasValue ? receipt.DocumentDate.Value.Year : (int?)null,
                    Currency = receipt.Currency ?? "Unknown currency"
                })
                .Select(group => new SpendAggregate(
                    group.Key.Year,
                    null,
                    group.Key.Currency,
                    group.Sum(receipt => receipt.TotalAmount ?? 0m),
                    group.Count()))
                .ToListAsync(cancellationToken),
            "vendor" => await query
                .GroupBy(receipt => new
                {
                    Vendor = receipt.Vendor ?? "Unknown vendor",
                    Currency = receipt.Currency ?? "Unknown currency"
                })
                .Select(group => new SpendAggregate(
                    null,
                    group.Key.Vendor,
                    group.Key.Currency,
                    group.Sum(receipt => receipt.TotalAmount ?? 0m),
                    group.Count()))
                .ToListAsync(cancellationToken),
            "category" => await query
                .GroupBy(receipt => new
                {
                    Category = receipt.Category ?? "Uncategorized",
                    Currency = receipt.Currency ?? "Unknown currency"
                })
                .Select(group => new SpendAggregate(
                    null,
                    group.Key.Category,
                    group.Key.Currency,
                    group.Sum(receipt => receipt.TotalAmount ?? 0m),
                    group.Count()))
                .ToListAsync(cancellationToken),
            _ => await query
                .GroupBy(receipt => receipt.Currency ?? "Unknown currency")
                .Select(group => new SpendAggregate(
                    null,
                    null,
                    group.Key,
                    group.Sum(receipt => receipt.TotalAmount ?? 0m),
                    group.Count()))
                .ToListAsync(cancellationToken)
        };

        if (aggregates.Count == 0)
        {
            if (snapshot is not null) await snapshot.CommitAsync(cancellationToken);
            return new("No reviewed receipt totals matched those filters.", [], 0,
                new(plan.Year, vendor, category, plan.GroupBy, true, [], []));
        }

        var matchingCount = aggregates.Sum(aggregate => aggregate.Count);
        var answer = string.Join("\n", aggregates
            .OrderBy(aggregate => aggregate.Year)
            .ThenBy(aggregate => aggregate.Label)
            .ThenBy(aggregate => aggregate.Currency)
            .Select(aggregate => FormatAggregate(plan.GroupBy, aggregate)));

        var sourceReceipts = await query
            .OrderByDescending(receipt => receipt.CreatedAtUtc)
            .ThenBy(receipt => receipt.Id)
            .Select(receipt => new
            {
                receipt.Id, receipt.FileName, receipt.Vendor, receipt.DocumentDate,
                receipt.Category, receipt.Currency, receipt.TotalAmount, receipt.ProcessingStatus
            })
            .ToListAsync(cancellationToken);

        var citations = sourceReceipts.Take(MaximumAggregateCitations).Select(receipt => new ReceiptQuestionCitation(
                receipt.Id,
                receipt.FileName,
                receipt.Vendor,
                receipt.DocumentDate,
                receipt.Currency,
                receipt.TotalAmount,
                receipt.ProcessingStatus))
            .ToArray();

        var totals = aggregates.OrderBy(aggregate => aggregate.Year)
            .ThenBy(aggregate => aggregate.Label).ThenBy(aggregate => aggregate.Currency)
            .Select(aggregate => new ReceiptCalculationTotal(
                AggregateLabel(plan.GroupBy, aggregate.Year, aggregate.Label), aggregate.Currency,
                aggregate.Amount.ToString("0.00##", CultureInfo.InvariantCulture), aggregate.Count))
            .ToArray();
        var contributions = sourceReceipts.Select(receipt => new ReceiptCalculationReceipt(
            receipt.Id, receipt.FileName, receipt.Vendor, receipt.DocumentDate, receipt.Category,
            receipt.Currency ?? "Unknown currency",
            receipt.TotalAmount!.Value.ToString("0.00##", CultureInfo.InvariantCulture),
            AggregateLabel(plan.GroupBy, receipt.DocumentDate?.Year,
                plan.GroupBy == "vendor" ? receipt.Vendor ?? "Unknown vendor" : receipt.Category ?? "Uncategorized")))
            .ToArray();
        if (snapshot is not null) await snapshot.CommitAsync(cancellationToken);

        return new ReceiptQuestionResponse(
            matchingCount > MaximumAggregateCitations
                ? $"{answer}\nShowing {MaximumAggregateCitations} of {matchingCount} matching receipt sources."
                : answer,
            citations,
            matchingCount,
            new(plan.Year, vendor, category, plan.GroupBy, true, totals, contributions));
    }

    private static string AggregateLabel(string groupBy, int? year, string? label) => groupBy switch
    {
        "year" => year?.ToString(CultureInfo.InvariantCulture) ?? "Unknown year",
        "vendor" or "category" => label ?? "Unknown",
        _ => "Total recorded spend"
    };

    private static string FormatAggregate(string groupBy, SpendAggregate aggregate)
    {
        var label = AggregateLabel(groupBy, aggregate.Year, aggregate.Label);

        var receiptLabel = aggregate.Count == 1 ? "reviewed receipt" : "reviewed receipts";
        return $"{label} / {aggregate.Currency}: {aggregate.Amount.ToString("0.00##", CultureInfo.InvariantCulture)} ({aggregate.Count} {receiptLabel})";
    }

    private sealed record SpendAggregate(int? Year, string? Label, string Currency, decimal Amount, int Count);

    private async Task<IReadOnlyList<ReceiptQuestionDocument>> FindContextAsync(
        string question,
        CancellationToken cancellationToken)
    {
        var documents = new List<Receipt>();
        var hasEmbeddings = await dbContext.Receipts
            .AsNoTracking()
            .AnyAsync(receipt => receipt.Embedding != null, cancellationToken);

        if (hasEmbeddings)
        {
            var vectorValues = await embeddingProvider.GenerateAsync(question, cancellationToken);
            if (vectorValues.Length != embeddingProvider.Dimensions)
            {
                throw new InvalidOperationException(
                    $"Query embedding has {vectorValues.Length} dimensions; provider reports {embeddingProvider.Dimensions}.");
            }

            var queryVector = new Vector(vectorValues);
            var semanticMatches = await dbContext.Receipts
                .AsNoTracking()
                .Include(receipt => receipt.LineItems)
                .Where(receipt => receipt.Embedding != null)
                .OrderBy(receipt => receipt.Embedding!.CosineDistance(queryVector))
                .ThenByDescending(receipt => receipt.CreatedAtUtc)
                .Take(MaximumSemanticMatches)
                .ToListAsync(cancellationToken);

            documents.AddRange(semanticMatches);
        }

        var remainingSlots = MaximumContextDocuments - documents.Count;
        if (remainingSlots > 0)
        {
            var includedIds = documents.Select(receipt => receipt.Id).ToArray();
            var remainingDocuments = await dbContext.Receipts
                .AsNoTracking()
                .Include(receipt => receipt.LineItems)
                .Where(receipt => !includedIds.Contains(receipt.Id))
                .OrderByDescending(receipt => receipt.CreatedAtUtc)
                .Take(remainingSlots)
                .ToListAsync(cancellationToken);

            documents.AddRange(remainingDocuments);
        }

        return documents.Select(receipt => new ReceiptQuestionDocument(
            receipt.Id,
            receipt.FileName,
            receipt.Vendor,
            receipt.DocumentDate,
            receipt.Currency,
            receipt.TotalAmount,
            receipt.Category,
            receipt.Description,
            receipt.ProcessingStatus.ToString(),
            receipt.LineItems
                .Take(MaximumLineItemsPerDocument)
                .Select(item => new ReceiptQuestionLineItem(
                    item.Description,
                    item.Quantity,
                    item.UnitPrice,
                    item.LineTotal,
                    item.Category))
                .ToArray()))
            .ToArray();
    }
}