using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using InvoiceManager.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace InvoiceManager.Api.Tests;

public sealed class ImprovementServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InvoiceDbContext _db = null!;
    private IDbContextTransaction _transaction = null!;
    private ImprovementService _service = null!;

    public async Task InitializeAsync()
    {
        _db = new InvoiceDbContext(new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector()).Options);
        _transaction = await _db.Database.BeginTransactionAsync();
        _service = new ImprovementService(_db);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Migration_PreservesLegacyReceiptAndLeavesSaleFactsUnset()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.Container.GetConnectionString());
        var name = $"ledger_migration_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        builder.Database = name;
        var receiptId = Guid.NewGuid();
        var expenseId = Guid.NewGuid();
        try
        {
            await using var legacy = new InvoiceDbContext(new DbContextOptionsBuilder<InvoiceDbContext>()
                .UseNpgsql(builder.ConnectionString, postgres => postgres.UseVector()).Options);
            await legacy.GetService<IMigrator>().MigrateAsync("20261105085634_AddProperties");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Receipts" ("Id", "PropertyId", "FileName", "FilePath", "ContentHash",
                    "ProcessingStatus", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({receiptId}, {Property.DefaultPropertyId}, {"synthetic.pdf"}, {"synthetic.pdf"},
                    {new string('A', 64)}, {"Completed"}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                """);
            await legacy.GetService<IMigrator>().MigrateAsync("20261105130755_AddDraftImprovementLedger");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ImprovementExpenses" ("Id", "PropertyId", "ReceiptId", "Description", "Type",
                    "Amount", "Currency", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({expenseId}, {Property.DefaultPropertyId}, {receiptId}, {"Legacy split"}, {"BasicImprovement"},
                    {12.3456m}, {"SEK"}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                """);
            await legacy.Database.MigrateAsync();
            var savedReceipt = await legacy.Receipts.SingleAsync();
            Assert.Equal(receiptId, savedReceipt.Id);
            Assert.Equal(Property.DefaultPropertyId, savedReceipt.PropertyId);
            Assert.Equal(ReceiptProcessingStatus.Completed, savedReceipt.ProcessingStatus);
            Assert.Equal(0, savedReceipt.ReviewVersion);
            var property = await legacy.Properties.SingleAsync();
            Assert.Null(property.AcquisitionDate);
            Assert.Null(property.SaleDate);
            Assert.Null(property.PropertyType);
            var savedExpense = await legacy.ImprovementExpenses.SingleAsync();
            Assert.Equal(expenseId, savedExpense.Id);
            Assert.Equal(12.3456m, savedExpense.Amount);
            Assert.Null(savedExpense.ConfirmedAtUtc);
            Assert.Null(savedExpense.ConfirmedReceiptVersion);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SaleFacts_SaveClearAndValidateDates()
    {
        var propertyId = Property.DefaultPropertyId;
        Assert.Equal(new SaleFactsRequest(null, null, null), await _service.GetSaleFactsAsync(propertyId, default));
        var facts = new SaleFactsRequest(PropertyType.SmallHouse, new(2000, 1, 1), new(2026, 1, 1));
        Assert.Equal(facts, await _service.SaveSaleFactsAsync(propertyId, facts, default));
        Assert.Equal(facts, await _service.GetSaleFactsAsync(propertyId, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveSaleFactsAsync(propertyId,
            facts with { SaleDate = new(1999, 1, 1) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveSaleFactsAsync(propertyId,
            facts with { PropertyType = (PropertyType)999 }, default));
        await _service.SaveSaleFactsAsync(propertyId, new(null, null, null), default);
        Assert.Equal(new SaleFactsRequest(null, null, null), await _service.GetSaleFactsAsync(propertyId, default));
        Assert.Null(await _service.GetSaleFactsAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DraftCrud_IsScopedAndAllowsIncompleteFields()
    {
        var receipt = await AddReceipt();
        var otherProperty = new Property { Name = "Other property" };
        _db.Properties.Add(otherProperty);
        await _db.SaveChangesAsync();
        var draft = await _service.SaveAsync(receipt.PropertyId, null, Request(receipt.Id), default);
        Assert.Contains(draft!.Warnings, warning => warning.Code == "IncompleteDraft");
        Assert.Null(draft.Amount);
        Assert.Empty((await _service.ListAsync(otherProperty.Id, default))!);
        Assert.Null(await _service.SaveAsync(otherProperty.Id, draft.Id, Request(receipt.Id), default));
        Assert.False(await _service.DeleteAsync(otherProperty.Id, draft.Id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(otherProperty.Id, null, Request(receipt.Id), default));
        var updated = await _service.SaveAsync(receipt.PropertyId, draft.Id,
            Request(receipt.Id) with { Amount = 25.1234m, Currency = "sek", ExpenseDate = new(2020, 1, 1), Type = ImprovementType.BasicImprovement }, default);
        Assert.Equal("25.1234", updated!.Amount);
        Assert.Contains(updated.Warnings, warning => warning.Code == "ClassificationPending");
        Assert.Contains(updated.Warnings, warning => warning.Code == "UnderAllocated");
        Assert.True(await _service.DeleteAsync(receipt.PropertyId, draft.Id, default));
        Assert.Empty((await _service.ListAsync(receipt.PropertyId, default))!);
    }

    [Fact]
    public async Task SplitAmounts_WarnWithoutCap_AndMixedCurrenciesAreNotSummed()
    {
        var receipt = await AddReceipt();
        await _service.SaveAsync(receipt.PropertyId, null, Request(receipt.Id) with { Amount = 70, Currency = "SEK" }, default);
        await _service.SaveAsync(receipt.PropertyId, null, Request(receipt.Id) with { Amount = 60, Currency = "SEK" }, default);
        var entries = (await _service.ListAsync(receipt.PropertyId, default))!;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, expense => Assert.Contains(expense.Warnings, warning => warning.Code == "OverAllocated"));
        await _service.SaveAsync(receipt.PropertyId, entries[0].Id,
            Request(receipt.Id) with { Amount = 60, Currency = "EUR" }, default);
        entries = (await _service.ListAsync(receipt.PropertyId, default))!;
        Assert.All(entries, expense => Assert.DoesNotContain(expense.Warnings, warning => warning.Code == "OverAllocated"));
        Assert.Contains(entries.SelectMany(expense => expense.Warnings), warning => warning.Code == "CurrencyMismatch");
        Assert.All(entries, expense => Assert.Contains(expense.Warnings, warning => warning.Code == "AllocationIncomplete"));
    }

    [Fact]
    public async Task ReviewedSourceCorrections_RefreshWarningsWithoutChangingDraft()
    {
        var receipt = await AddReceipt();
        var draft = await _service.SaveAsync(receipt.PropertyId, null,
            Request(receipt.Id) with { Amount = 90, Currency = "SEK" }, default);
        receipt.TotalAmount = null;
        receipt.Currency = null;
        receipt.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        await _db.SaveChangesAsync();
        var result = Assert.Single((await _service.ListAsync(receipt.PropertyId, default))!);
        Assert.Equal(draft!.Amount, result.Amount);
        Assert.Contains(result.Warnings, warning => warning.Code == "IncompleteSource");
        Assert.Contains(result.Warnings, warning => warning.Code == "UnreviewedSource");
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(
            receipt.PropertyId, draft.Id, Request(receipt.Id), default));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.12345")]
    [InlineData("100000000000000")]
    public async Task InvalidAmount_IsRejectedWithoutSaving(string value)
    {
        var receipt = await AddReceipt();
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(
            receipt.PropertyId, null, Request(receipt.Id) with { Amount = amount }, default));
        Assert.Empty(await _db.ImprovementExpenses.ToListAsync());
    }

    [Fact]
    public async Task InvalidDraftFieldsAndUnreviewedEvidence_AreRejected()
    {
        var receipt = await AddReceipt();
        DraftExpenseRequest[] invalid =
        [
            Request(receipt.Id) with { Description = " " },
            Request(receipt.Id) with { Description = new string('A', 1001) },
            Request(receipt.Id) with { Currency = "XYZQ" },
            Request(receipt.Id) with { Type = (ImprovementType)999 },
            Request(receipt.Id) with { ReceiptId = Guid.Empty },
            Request(receipt.Id) with { Notes = new string('A', 4001) }
        ];
        foreach (var request in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(receipt.PropertyId, null, request, default));
        receipt.ProcessingStatus = ReceiptProcessingStatus.Pending;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(receipt.PropertyId, null, Request(receipt.Id), default));
        Assert.Empty(await _db.ImprovementExpenses.ToListAsync());
    }

    [Fact]
    public async Task DatabaseRejectsCrossPropertyEvidence()
    {
        var receipt = await AddReceipt();
        var otherProperty = new Property { Name = "Other property" };
        _db.Properties.Add(otherProperty);
        await _db.SaveChangesAsync();
        _db.ImprovementExpenses.Add(new()
        {
            PropertyId = otherProperty.Id, ReceiptId = receipt.Id, Description = "Invalid link"
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal("FK_ImprovementExpenses_Receipts_PropertyId_ReceiptId", postgres.ConstraintName);
    }

    private async Task<Receipt> AddReceipt()
    {
        var receipt = new Receipt
        {
            PropertyId = Property.DefaultPropertyId, FileName = "synthetic.pdf", FilePath = $"{Guid.NewGuid():N}.pdf",
            ContentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", ProcessingStatus = ReceiptProcessingStatus.Completed,
            Vendor = "Synthetic vendor", TotalAmount = 100, Currency = "SEK"
        };
        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync();
        return receipt;
    }

    private static DraftExpenseRequest Request(Guid receiptId) =>
        new(receiptId, "Synthetic improvement", null, ImprovementType.Unclassified, null, null, null);

    private static ReceiptExpenseRequest Split(decimal amount = 100m) => new(
        null, "Synthetic replacement", new(2026, 1, 1), ImprovementType.RepairsAndMaintenance,
        amount, "SEK", null, "Replaced worn floor with equivalent material", null, 0m, 0m, null, true);

    private static UpdateReceiptReviewRequest Review(Receipt receipt, params ReceiptExpenseRequest[] splits) => new()
    {
        Vendor = receipt.Vendor, DocumentDate = new(2026, 1, 1), Currency = receipt.Currency,
        TotalAmount = receipt.TotalAmount, MarkReviewed = true, ReviewVersion = receipt.ReviewVersion,
        Expenses = [.. splits],
    };

    [Fact]
    public async Task FirstReview_SavesMixedSplitsAtomically_AndPreservesStableIds()
    {
        var receipt = await AddReceipt();
        receipt.ProcessingStatus = ReceiptProcessingStatus.Pending;
        await _db.SaveChangesAsync();
        var review = new ReceiptReviewService(_db);
        var result = await review.ReviewAsync(receipt.Id, Review(receipt, Split(75),
            Split(25) with { Type = ImprovementType.Excluded, ExclusionReason = "Unrelated personal purchase" }), default);
        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.All(result.Receipt!.Expenses, expense => Assert.NotNull(expense.ConfirmedAtUtc));
        var ids = result.Receipt.Expenses.Select(expense => expense.Id).Order().ToArray();
        var request = Review(receipt, result.Receipt.Expenses.Select(expense => Split(decimal.Parse(expense.Amount!,
            System.Globalization.CultureInfo.InvariantCulture)) with
        {
            Id = expense.Id, Type = expense.Type, ExclusionReason = expense.ExclusionReason
        }).ToArray()) with { LineItems = [new() { Description = "Corrected source line", LineTotal = 100 }] };
        result = await review.ReviewAsync(receipt.Id, request, default);
        Assert.Equal(ids, result.Receipt!.Expenses.Select(expense => expense.Id).Order());
        Assert.Equal(2, await _db.ImprovementExpenses.CountAsync());
        var readiness = await _service.GetReadinessAsync(receipt.PropertyId, default);
        Assert.Equal(2, readiness!.ConfirmedExpenses);
        Assert.Equal(1, readiness.ExcludedExpenses);
        Assert.False(readiness.CanGenerateReport);
        Assert.Contains(readiness.MissingFacts, fact => fact.Contains("wear"));
    }

    [Theory]
    [InlineData("unclassified")]
    [InlineData("unconfirmed")]
    [InlineData("missingWork")]
    [InlineData("excludedReason")]
    [InlineData("overlap")]
    [InlineData("unallocated")]
    [InlineData("currency")]
    [InlineData("foreignId")]
    public async Task InvalidApproval_LeavesMetadataAndExpensesUnchanged(string scenario)
    {
        var receipt = await AddReceipt();
        receipt.ProcessingStatus = ReceiptProcessingStatus.Pending;
        await _db.SaveChangesAsync();
        var split = scenario switch
        {
            "unclassified" => Split() with { Type = ImprovementType.Unclassified },
            "unconfirmed" => Split() with { Confirm = false },
            "missingWork" => Split() with { WorkDetails = null },
            "excludedReason" => Split() with { Type = ImprovementType.Excluded },
            "overlap" => Split(101),
            "unallocated" => Split(99),
            "currency" => Split() with { Currency = "EUR" },
            _ => Split() with { Id = Guid.NewGuid() },
        };
        var result = await new ReceiptReviewService(_db).ReviewAsync(receipt.Id,
            Review(receipt, split) with { Vendor = "Must not save" }, default);
        Assert.Equal(ReceiptReviewStatus.Invalid, result.Status);
        _db.ChangeTracker.Clear();
        var stored = await _db.Receipts.SingleAsync(item => item.Id == receipt.Id);
        Assert.Equal("Synthetic vendor", stored.Vendor);
        Assert.Equal(ReceiptProcessingStatus.Pending, stored.ProcessingStatus);
        Assert.Equal(0, stored.ReviewVersion);
        Assert.Empty(await _db.ImprovementExpenses.ToListAsync());
    }

    [Fact]
    public async Task DraftSave_AllowsIncompleteOverlap_AndLegacyWritesInvalidateConfirmation()
    {
        var receipt = await AddReceipt();
        var review = new ReceiptReviewService(_db);
        var draft = await review.ReviewAsync(receipt.Id, Review(receipt, Split(101) with
            { Confirm = false, Type = ImprovementType.Unclassified, RotAmount = null }) with { MarkReviewed = false }, default);
        var stored = Assert.Single(draft.Receipt!.Expenses);
        Assert.Null(stored.ConfirmedAtUtc);
        var approved = await review.ReviewAsync(receipt.Id, Review(receipt, Split() with { Id = stored.Id }), default);
        var confirmedAt = Assert.Single(approved.Receipt!.Expenses).ConfirmedAtUtc;
        var unchanged = await review.ReviewAsync(receipt.Id, Review(receipt, Split() with { Id = stored.Id }), default);
        Assert.Equal(confirmedAt, Assert.Single(unchanged.Receipt!.Expenses).ConfirmedAtUtc);
        await _service.SaveAsync(receipt.PropertyId, stored.Id, Request(receipt.Id) with
            { Amount = 100, Currency = "SEK" }, default);
        Assert.Null((await _service.ListAsync(receipt.PropertyId, default))![0].ReviewFacts!.ConfirmedAtUtc);
        var stale = await review.ReviewAsync(receipt.Id, Review(receipt, Split() with { Id = stored.Id })
            with { ReviewVersion = approved.Receipt.ReviewVersion }, default);
        Assert.Equal(ReceiptReviewStatus.Conflict, stale.Status);
        var removed = await review.ReviewAsync(receipt.Id, Review(receipt) with { MarkReviewed = false }, default);
        Assert.Equal(ReceiptReviewStatus.Updated, removed.Status);
        Assert.Empty(removed.Receipt!.Expenses);
        Assert.Empty(await _db.ImprovementExpenses.ToListAsync());
        Assert.Contains((await _service.GetReadinessAsync(receipt.PropertyId, default))!.MissingFacts,
            fact => fact.Contains("classifications"));
    }

    [Fact]
    public async Task MetadataOnlyLegacyEdit_InvalidatesTaxConfirmationWithoutResettingReceiptApproval()
    {
        var receipt = await AddReceipt();
        var review = new ReceiptReviewService(_db);
        await review.ReviewAsync(receipt.Id, Review(receipt, Split()), default);
        var result = await review.ReviewAsync(receipt.Id, Review(receipt) with
            { Expenses = null, TotalAmount = 200 }, default);
        Assert.Equal(ReceiptProcessingStatus.Completed, result.Receipt!.Status);
        Assert.Null(Assert.Single(result.Receipt.Expenses).ConfirmedAtUtc);
        Assert.Equal(1, (await _service.GetReadinessAsync(receipt.PropertyId, default))!.PendingExpenses);
    }

    [Fact]
    public async Task ZeroValueExcludedReceipt_CanBeConfirmedWithoutInventingDeductibleAmounts()
    {
        var receipt = await AddReceipt();
        receipt.TotalAmount = 0;
        await _db.SaveChangesAsync();
        var result = await new ReceiptReviewService(_db).ReviewAsync(receipt.Id, Review(receipt, Split(0) with
            { Type = ImprovementType.Excluded, ExclusionReason = "No expense paid" }), default);
        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.NotNull(Assert.Single(result.Receipt!.Expenses).ConfirmedAtUtc);
    }

    [Fact]
    public async Task MvpApproval_DoesNotRequireAdjustments_AndOmittedValuesPreserveSavedFacts()
    {
        var receipt = await AddReceipt();
        receipt.Currency = null;
        await _db.SaveChangesAsync();
        var review = new ReceiptReviewService(_db);
        var input = Split() with { Currency = null, RotAmount = null, OtherCompensationAmount = null };
        var result = await review.ReviewAsync(receipt.Id, Review(receipt, input), default);
        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        var portion = Assert.Single(result.Receipt!.Expenses);
        Assert.Equal("SEK", result.Receipt.Currency);
        Assert.Equal("SEK", portion.Currency);
        Assert.Null(portion.RotAmount);
        Assert.Null(portion.OtherCompensationAmount);
        result = await review.ReviewAsync(receipt.Id, Review(receipt, input with
            { Id = portion.Id, RotAmount = 12.3456m, OtherCompensationAmount = 5, AdjustmentsAlreadyInAmount = true }), default);
        var timestamp = Assert.Single(result.Receipt!.Expenses).ConfirmedAtUtc;
        result = await review.ReviewAsync(receipt.Id, Review(receipt, input with { Id = portion.Id }), default);
        portion = Assert.Single(result.Receipt!.Expenses);
        Assert.Equal("12.3456", portion.RotAmount);
        Assert.Equal("5.00", portion.OtherCompensationAmount);
        Assert.True(portion.AdjustmentsAlreadyInAmount);
        Assert.Equal(timestamp, portion.ConfirmedAtUtc);
    }

    [Fact]
    public async Task ForeignCurrency_PreservedInDrafts_BlockedForApproval_AndExplicitCorrectionDoesNotConvert()
    {
        var receipt = await AddReceipt();
        receipt.Currency = "EUR";
        receipt.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        await _db.SaveChangesAsync();
        var review = new ReceiptReviewService(_db);
        var input = Split() with { Currency = "EUR" };
        var invalid = await review.ReviewAsync(receipt.Id, Review(receipt, input), default);
        Assert.Equal(ReceiptReviewStatus.Invalid, invalid.Status);
        Assert.Contains(invalid.ValidationErrors, error => error.Contains("SEK"));
        var draft = await review.ReviewAsync(receipt.Id, Review(receipt, input with { Confirm = false })
            with { MarkReviewed = false }, default);
        Assert.Equal("EUR", draft.Receipt!.Currency);
        var portion = Assert.Single(draft.Receipt.Expenses);
        var unchanged = await review.ReviewAsync(receipt.Id, Review(receipt, input with
            { Id = portion.Id, Currency = null, Confirm = false }) with { Currency = null, MarkReviewed = false }, default);
        Assert.Equal("EUR", Assert.Single(unchanged.Receipt!.Expenses).Currency);
        var corrected = await review.ReviewAsync(receipt.Id, Review(receipt, input with
            { Id = portion.Id, Currency = "SEK", Confirm = false }) with { Currency = "SEK", MarkReviewed = false }, default);
        Assert.Equal("SEK", corrected.Receipt!.Currency);
        Assert.Equal(100m, corrected.Receipt.TotalAmount);
        Assert.Null(Assert.Single(corrected.Receipt.Expenses).ConfirmedAtUtc);
    }

    [Fact]
    public async Task ExpenseReport_ExactTotalsExclusionsPartialCoverageAndEncoding_ArePropertyScoped()
    {
        var receipt = await AddReceipt();
        receipt.TotalAmount = 100.1234m;
        receipt.Vendor = "<script>alert('vendor')</script>";
        receipt.FileName = "<img onerror=alert(1)>.pdf";
        await _db.SaveChangesAsync();
        var result = await new ReceiptReviewService(_db).ReviewAsync(receipt.Id,
            Review(receipt, Split(75.1234m) with
                { ExpenseDate = new(2023, 5, 1), Description = "<script>expense</script>", RotAmount = 25 },
                Split(25) with { Type = ImprovementType.Excluded, ExclusionReason = "<b>Personal</b>" }), default);
        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        var unresolved = await AddReceipt();
        unresolved.Currency = "EUR";
        unresolved.ImprovementExpenses.Add(new()
        {
            ReceiptId = unresolved.Id, PropertyId = unresolved.PropertyId, Description = "Foreign legacy",
            Amount = 100, Currency = "EUR", ExpenseDate = new(2026, 1, 1), Type = ImprovementType.BasicImprovement,
            WorkDetails = "Legacy work", ConfirmedAtUtc = DateTimeOffset.UtcNow, ConfirmedReceiptVersion = 0
        });
        _db.ImprovementExpenses.AddRange(unresolved.ImprovementExpenses);
        var other = new Property { Name = "Another property" };
        _db.Properties.Add(other);
        await _db.SaveChangesAsync();
        var service = new ExpenseReportService(_db);
        var report = await service.GenerateAsync(receipt.PropertyId, default);
        Assert.Equal("75.1234", report!.IncludedTotal);
        Assert.Equal("25.00", report.ExcludedTotal);
        Assert.Equal(2, report.ReceiptCount);
        Assert.Equal(1, report.ConfirmedReceiptCount);
        Assert.Equal(2023, Assert.Single(report.Included).ExpenseDate.Year);
        Assert.Equal("75.1234", Assert.Single(report.Totals, total => total.Year == 2023).Amount);
        Assert.Equal("EUR", Assert.Single(report.Unresolved).Currency);
        Assert.Contains(report.Unresolved[0].Reasons, reason => reason.Contains("SEK"));
        Assert.Contains("Partial summary", report.Html);
        Assert.Contains("&lt;script&gt;", report.Html);
        Assert.DoesNotContain("<script>", report.Html);
        Assert.DoesNotContain("<img ", report.Html);
        Assert.Contains(receipt.Id.ToString(), report.Html);
        Assert.Contains("not a tax deduction", report.Html);
        var empty = await service.GenerateAsync(other.Id, default);
        Assert.Equal(0, empty!.ReceiptCount);
        Assert.Equal("0.00", empty.IncludedTotal);
        Assert.Contains("No confirmed receipt amounts", empty.Html);
        Assert.DoesNotContain(receipt.Id.ToString(), empty.Html);
        Assert.Null(await service.GenerateAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(Guid.Empty, default));
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("unallocated")]
    [InlineData("pending")]
    [InlineData("stale")]
    [InlineData("missingFacts")]
    public async Task ExpenseReport_WholeReceiptOutsideTotals_WhenAnyPortionIsUnresolved(string scenario)
    {
        var receipt = await AddReceipt();
        var review = new ReceiptReviewService(_db);
        var saved = await review.ReviewAsync(receipt.Id, Review(receipt, Split(60), Split(40)), default);
        Assert.Equal(ReceiptReviewStatus.Updated, saved.Status);
        var portions = receipt.ImprovementExpenses;
        switch (scenario)
        {
            case "unconfirmed": portions[0].ConfirmedAtUtc = null; break;
            case "unallocated": portions[0].Amount = 59; break;
            case "pending": receipt.ProcessingStatus = ReceiptProcessingStatus.NeedsReview; break;
            case "stale": portions[0].ConfirmedReceiptVersion = -1; break;
            default: portions[0].WorkDetails = null; break;
        }
        await _db.SaveChangesAsync();
        var report = await new ExpenseReportService(_db).GenerateAsync(receipt.PropertyId, default);
        Assert.Empty(report!.Included);
        Assert.Empty(report.Totals);
        Assert.Equal("0.00", report.IncludedTotal);
        Assert.Equal(2, Assert.Single(report.Unresolved).Portions.Count);
    }

    [Fact]
    public async Task ExpenseReportEndpoints_ReturnPreviewAndSafeHtmlAttachment_WithExplicitScope()
    {
        var property = new Property { Name = "Synthetic report property" };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_db);
        builder.Services.AddScoped<ImprovementService>();
        builder.Services.AddScoped<ExpenseReportService>();
        await using var app = builder.Build();
        app.MapImprovementEndpoints();
        await app.StartAsync();
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var route = $"/api/properties/{property.Id}/expense-report";
            using var preview = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Contains("Synthetic report property", await preview.Content.ReadAsStringAsync());
            using var download = await client.GetAsync(route + "/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("text/html", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Contains($"expense-summary-{property.Id:N}.html",
                download.Content.Headers.ContentDisposition.ToString());
            Assert.Contains("<!doctype html>", await download.Content.ReadAsStringAsync());
            using var invalid = await client.GetAsync($"/api/properties/{Guid.Empty}/expense-report");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var unknown = await client.GetAsync($"/api/properties/{Guid.NewGuid()}/expense-report/download");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ConcurrentReviewConflict_RollsBackSplitAndMetadataWrites()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector()).Options;
        await using var first = new InvoiceDbContext(options);
        await using var second = new InvoiceDbContext(options);
        var receipt = new Receipt
        {
            PropertyId = Property.DefaultPropertyId, FileName = "synthetic-race.pdf", FilePath = "synthetic-race.pdf",
            ContentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", Vendor = "Original synthetic vendor",
            Currency = "SEK", TotalAmount = 100, ProcessingStatus = ReceiptProcessingStatus.Pending
        };
        first.Receipts.Add(receipt);
        await first.SaveChangesAsync();
        try
        {
            var staleReceipt = await second.Receipts.SingleAsync(item => item.Id == receipt.Id);
            var saved = await new ReceiptReviewService(first).ReviewAsync(receipt.Id, Review(receipt, Split()), default);
            Assert.Equal(ReceiptReviewStatus.Updated, saved.Status);
            var conflict = await new ReceiptReviewService(second).ReviewAsync(receipt.Id,
                Review(staleReceipt, Split() with { Description = "Must roll back" })
                    with { Vendor = "Must roll back", ReviewVersion = 0 }, default);
            Assert.Equal(ReceiptReviewStatus.Conflict, conflict.Status);
            first.ChangeTracker.Clear();
            var stored = await first.Receipts.Include(item => item.ImprovementExpenses).SingleAsync(item => item.Id == receipt.Id);
            Assert.Equal("Original synthetic vendor", stored.Vendor);
            Assert.Equal("Synthetic replacement", Assert.Single(stored.ImprovementExpenses).Description);
            Assert.Equal(1, stored.ReviewVersion);
        }
        finally
        {
            first.ChangeTracker.Clear();
            await first.ImprovementExpenses.Where(expense => expense.ReceiptId == receipt.Id).ExecuteDeleteAsync();
            await first.ReceiptLineItems.Where(item => item.ReceiptId == receipt.Id).ExecuteDeleteAsync();
            await first.Receipts.Where(item => item.Id == receipt.Id).ExecuteDeleteAsync();
        }
    }
}
