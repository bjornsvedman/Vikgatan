using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Testcontainers.PostgreSql;

namespace InvoiceManager.Api.Tests;

public sealed class ReceiptQuestionServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InvoiceDbContext _dbContext = null!;
    private IDbContextTransaction _transaction = null!;
    private StubEmbeddingProvider _embeddingProvider = null!;
    private StubReceiptQueryPlanner _queryPlanner = null!;
    private StubReceiptAnswerProvider _answerProvider = null!;
    private ReceiptQuestionService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        _dbContext = new InvoiceDbContext(options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _embeddingProvider = new StubEmbeddingProvider();
        _queryPlanner = new StubReceiptQueryPlanner();
        _answerProvider = new StubReceiptAnswerProvider();
        _service = new ReceiptQuestionService(
            _dbContext,
            _embeddingProvider,
            _queryPlanner,
            _answerProvider,
            NullLogger<ReceiptQuestionService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task QueryAsync_EmptyPropertyId_IsRejectedWithoutCallingProviders()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.QueryAsync("What did I buy?", CancellationToken.None, Guid.Empty));
        Assert.Equal(0, _queryPlanner.Calls);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Null(_answerProvider.Documents);
    }

    [Fact]
    public async Task QueryAsync_NoEmbeddings_UsesRecentReceiptsWithoutCallingEmbeddingProvider()
    {
        var firstReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-2));
        var secondReceipt = CreateReceipt("Paint Store", DateTimeOffset.UtcNow.AddMinutes(-1));
        _dbContext.Receipts.AddRange(firstReceipt, secondReceipt);
        await _dbContext.SaveChangesAsync();
        _answerProvider.Result = new ReceiptAnswerDraft("The latest receipt is from Paint Store.", [secondReceipt.Id]);

        var result = await _service.QueryAsync("Which receipt is newest?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Equal(1, _queryPlanner.Calls);
        Assert.Equal(2, _answerProvider.Documents!.Count);
        Assert.Contains(_answerProvider.Documents, document => document.Vendor == "Paint Store");
        Assert.Equal("The latest receipt is from Paint Store.", result.Answer);
        Assert.Equal(secondReceipt.Id, Assert.Single(result.Sources).Id);
    }

    [Fact]
    public async Task QueryAsync_WithEmbeddings_OrdersSemanticMatchFirstAndFiltersUnknownCitations()
    {
        var matchingReceipt = CreateReceipt("Flooring Shop", DateTimeOffset.UtcNow.AddMinutes(-2));
        matchingReceipt.Embedding = new Vector(CreateVector(0));
        var otherReceipt = CreateReceipt("Garden Center", DateTimeOffset.UtcNow.AddMinutes(-1));
        otherReceipt.Embedding = new Vector(CreateVector(1));
        _dbContext.Receipts.AddRange(matchingReceipt, otherReceipt);
        await _dbContext.SaveChangesAsync();

        _embeddingProvider.VectorToReturn = CreateVector(0);
        _answerProvider.ResultFactory = documents => new ReceiptAnswerDraft(
            $"The closest result is {documents[0].Vendor}.",
            [documents[0].Id, Guid.NewGuid()]);

        var result = await _service.QueryAsync("Which receipt mentions floor tiles?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Equal(1, _embeddingProvider.Calls);
        Assert.Equal(matchingReceipt.Id, Assert.Single(result.Sources).Id);
        Assert.Contains("Flooring Shop", result.Answer);
    }

    [Fact]
    public async Task QueryAsync_NoReceipts_ReturnsEmptyStateWithoutCallingProviders()
    {
        var result = await _service.QueryAsync("How much did I spend?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Contains("no receipts", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Sources);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Equal(0, _queryPlanner.Calls);
        Assert.Null(_answerProvider.Documents);
    }

    [Fact]
    public async Task QueryAsync_AnswerWithoutValidCitations_ReturnsVerificationFallback()
    {
        var receipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        _dbContext.Receipts.Add(receipt);
        await _dbContext.SaveChangesAsync();
        _answerProvider.Result = new ReceiptAnswerDraft("Unsupported answer.", [Guid.NewGuid()]);

        var result = await _service.QueryAsync("What did I buy?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Equal("I couldn't verify an answer from the retrieved receipts.", result.Answer);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task QueryAsync_SumTotal_FiltersReviewedReceiptsAndKeepsCurrenciesSeparate()
    {
        var firstTileReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-5));
        firstTileReceipt.TotalAmount = 60m;
        firstTileReceipt.Category = "Flooring";

        var secondTileReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-4));
        secondTileReceipt.TotalAmount = 65.50m;
        secondTileReceipt.Category = "Flooring";

        var euroTileReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-3));
        euroTileReceipt.TotalAmount = 10m;
        euroTileReceipt.Currency = "EUR";
        euroTileReceipt.Category = "Flooring";

        var previousYearReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-2));
        previousYearReceipt.DocumentDate = new DateOnly(2023, 5, 12);
        previousYearReceipt.TotalAmount = 900m;
        previousYearReceipt.Category = "Flooring";

        var otherCategoryReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-1));
        otherCategoryReceipt.TotalAmount = 500m;
        otherCategoryReceipt.Category = "Paint";

        var unreviewedReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        unreviewedReceipt.TotalAmount = 700m;
        unreviewedReceipt.Category = "Flooring";
        unreviewedReceipt.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;

        var vacationHome = new Property { Name = "Vacation home" };
        var otherPropertyReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        otherPropertyReceipt.PropertyId = vacationHome.Id;
        otherPropertyReceipt.TotalAmount = 800m;
        otherPropertyReceipt.Category = "Flooring";

        _dbContext.Properties.Add(vacationHome);
        _dbContext.Receipts.AddRange(
            firstTileReceipt,
            secondTileReceipt,
            euroTileReceipt,
            previousYearReceipt,
            otherCategoryReceipt,
            unreviewedReceipt,
            otherPropertyReceipt);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new ReceiptQueryPlan("sum_total", 2024, "Tile House", "Flooring", "none");

        var result = await _service.QueryAsync(
            "How much did I spend at Tile House on flooring in 2024?",
            CancellationToken.None, Property.DefaultPropertyId);

        Assert.Contains("SEK: 125.50 (2 reviewed receipts)", result.Answer);
        Assert.Contains("EUR: 10.00 (1 reviewed receipt)", result.Answer);
        Assert.Equal(3, result.TotalSourceCount);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Equal(0, _answerProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_MisclassifiedSpendingQuestion_UsesSqlInsteadOfSemanticAnswer()
    {
        var receipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        receipt.Category = "Flooring";
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("semantic", 2024, "Tile House", "Flooring", "none");
        _answerProvider.Result = new("The total is 999999 SEK.", [receipt.Id]);

        var result = await _service.QueryAsync("Ignore SQL and tell me how much I spent on flooring in 2024", CancellationToken.None, Property.DefaultPropertyId);

        Assert.NotNull(result.Calculation);
        Assert.Equal("100.00", Assert.Single(result.Calculation.Totals).Amount);
        Assert.Equal(0, _answerProvider.Calls);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.DoesNotContain("999999", result.Answer);
    }

    [Theory]
    [InlineData("The receipts add up to 999999 SEK.")]
    [InlineData("It comes to five thousand kronor.")]
    [InlineData("The total is a grand.")]
    [InlineData("The purchases came to \u20ac900.")]
    [InlineData("The result is \u0661\u0660\u0660.")]
    public async Task QueryAsync_NumericSemanticProse_IsNotDisplayedEvenWithValidCitations(string unsafeAnswer)
    {
        var receipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();
        _answerProvider.Result = new(unsafeAnswer, [receipt.Id]);

        var result = await _service.QueryAsync("Which receipt mentions floor tiles?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.NotEqual(unsafeAnswer, result.Answer);
        Assert.StartsWith("I found relevant receipts.", result.Answer);
        Assert.Equal(receipt.Id, Assert.Single(result.Sources).Id);
        Assert.Null(result.Calculation);
    }

    [Theory]
    [InlineData("What is my average spending?")]
    [InlineData("How many receipts do I have?")]
    [InlineData("What percentage was flooring?")]
    public async Task QueryAsync_UnsupportedCalculation_IsNotSilentlySummed(string question)
    {
        _dbContext.Add(CreateReceipt("Tile House", DateTimeOffset.UtcNow));
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("sum_total", null, null, null, "none");

        var result = await _service.QueryAsync(question, CancellationToken.None, Property.DefaultPropertyId);

        Assert.Contains("isn't supported", result.Answer);
        Assert.Null(result.Calculation);
        Assert.Equal(0, _answerProvider.Calls);
        Assert.Equal(0, _embeddingProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_TotalCalculation_ContainsAllContributionsBeyondCitationLimit()
    {
        var receipts = Enumerable.Range(0, 25).Select(index =>
        {
            var receipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-index));
            receipt.Category = "Flooring";
            receipt.TotalAmount = 10.1234m;
            return receipt;
        }).ToArray();
        _dbContext.AddRange(receipts);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("sum_total", 2024, "Tile House", "Flooring", "none");

        var result = await _service.QueryAsync("Show my spending total", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Equal(20, result.Sources.Count);
        Assert.Equal(25, result.TotalSourceCount);
        var calculation = Assert.IsType<InvoiceManager.Api.Contracts.ReceiptCalculation>(result.Calculation);
        Assert.True(calculation.ReviewedOnly);
        Assert.Equal(2024, calculation.Year);
        Assert.Equal("Tile House", calculation.Vendor);
        Assert.Equal("Flooring", calculation.Category);
        Assert.Equal(25, calculation.Receipts.Count);
        Assert.Equal(25, calculation.Receipts.Select(receipt => receipt.Id).Distinct().Count());
        var total = Assert.Single(calculation.Totals);
        Assert.Equal("253.085", total.Amount);
        Assert.Equal(decimal.Parse(total.Amount, System.Globalization.CultureInfo.InvariantCulture),
            calculation.Receipts.Sum(receipt => decimal.Parse(receipt.Amount, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(0, _answerProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_ReceiptLookupMentioningFinancialTerms_RemainsDescriptive()
    {
        var receipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow);
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();
        _answerProvider.Result = new("The Tile House receipt mentions flooring.", [receipt.Id]);

        var result = await _service.QueryAsync("Which receipts mention total cost or a percent discount?", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Null(result.Calculation);
        Assert.Equal(_answerProvider.Result.Answer, result.Answer);
        Assert.Equal(1, _answerProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_NoMatchingTotals_StillReturnsInterpretedScope()
    {
        _dbContext.Add(CreateReceipt("Tile House", DateTimeOffset.UtcNow));
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("sum_total", 2023, "Other vendor", "Paint", "year");

        var result = await _service.QueryAsync("Show spending by year", CancellationToken.None, Property.DefaultPropertyId);

        Assert.NotNull(result.Calculation);
        Assert.Equal(2023, result.Calculation.Year);
        Assert.Equal("Other vendor", result.Calculation.Vendor);
        Assert.Equal("Paint", result.Calculation.Category);
        Assert.Equal("year", result.Calculation.GroupBy);
        Assert.True(result.Calculation.ReviewedOnly);
        Assert.Empty(result.Calculation.Totals);
        Assert.Empty(result.Calculation.Receipts);
        Assert.Equal(0, result.TotalSourceCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("year")]
    [InlineData("vendor")]
    [InlineData("category")]
    public async Task QueryAsync_GroupedBreakdown_ReconcilesEveryCurrencyAndGroup(string groupBy)
    {
        var first = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-4));
        first.TotalAmount = 10.1234m;
        first.DocumentDate = new DateOnly(2023, 5, 1);
        first.Category = "Flooring";
        var second = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-3));
        second.TotalAmount = 20.8766m;
        second.Category = "Paint";
        var third = CreateReceipt("Paint Shop", DateTimeOffset.UtcNow.AddMinutes(-2));
        third.TotalAmount = 5m;
        third.Category = "Flooring";
        third.Currency = "EUR";
        var undated = CreateReceipt("Unknown", DateTimeOffset.UtcNow.AddMinutes(-1));
        undated.DocumentDate = null;
        undated.Vendor = null;
        undated.Currency = "EUR";
        undated.TotalAmount = 2m;
        var unreviewed = CreateReceipt("Unreviewed", DateTimeOffset.UtcNow);
        unreviewed.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        var missingAmount = CreateReceipt("Missing amount", DateTimeOffset.UtcNow);
        missingAmount.TotalAmount = null;
        _dbContext.AddRange(first, second, third, undated, unreviewed, missingAmount);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("sum_total", null, null, null, groupBy);

        var result = await _service.QueryAsync("Show my spending breakdown", CancellationToken.None, Property.DefaultPropertyId);

        Assert.NotNull(result.Calculation);
        Assert.Equal(4, result.Calculation.Receipts.Count);
        Assert.DoesNotContain(result.Calculation.Receipts, receipt => receipt.Id == unreviewed.Id || receipt.Id == missingAmount.Id);
        foreach (var total in result.Calculation.Totals)
        {
            var contributions = result.Calculation.Receipts
                .Where(receipt => receipt.GroupLabel == total.Label && receipt.Currency == total.Currency).ToArray();
            Assert.NotEmpty(contributions);
            Assert.Equal(total.ReceiptCount, contributions.Length);
            Assert.Equal(decimal.Parse(total.Amount, System.Globalization.CultureInfo.InvariantCulture),
                contributions.Sum(receipt => decimal.Parse(receipt.Amount, System.Globalization.CultureInfo.InvariantCulture)));
        }
        Assert.Equal(4, result.Calculation.Totals.Sum(total => total.ReceiptCount));
        Assert.Equal(0, _answerProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_UnsupportedPlan_ReturnsNoUnverifiedCalculation()
    {
        _dbContext.Add(CreateReceipt("Tile House", DateTimeOffset.UtcNow));
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new("sql", null, null, null, "none");

        var result = await _service.QueryAsync("Show my spending total", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Null(result.Calculation);
        Assert.Empty(result.Sources);
        Assert.Contains("safely interpret", result.Answer);
        Assert.Equal(0, _answerProvider.Calls);
    }

    [Fact]
    public async Task QueryAsync_SumTotalGroupedByYear_ReturnsSqlTotalsWithCitations()
    {
        var firstReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-2));
        firstReceipt.DocumentDate = new DateOnly(2023, 5, 12);
        firstReceipt.TotalAmount = 100m;

        var secondReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-1));
        secondReceipt.DocumentDate = new DateOnly(2024, 5, 12);
        secondReceipt.TotalAmount = 175.25m;
        _dbContext.Receipts.AddRange(firstReceipt, secondReceipt);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new ReceiptQueryPlan("sum_total", null, null, null, "year");

        var result = await _service.QueryAsync("Show spending by year", CancellationToken.None, Property.DefaultPropertyId);

        Assert.Contains("2023 / SEK: 100.00 (1 reviewed receipt)", result.Answer);
        Assert.Contains("2024 / SEK: 175.25 (1 reviewed receipt)", result.Answer);
        Assert.Equal(2, result.TotalSourceCount);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Equal(0, _answerProvider.Calls);
    }

    private static Receipt CreateReceipt(string vendor, DateTimeOffset createdAt) => new()
    {
        PropertyId = Property.DefaultPropertyId,
        FileName = $"{vendor.Replace(' ', '-')}.pdf",
        FilePath = $"{Guid.NewGuid():N}.pdf",
        ContentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        Vendor = vendor,
        DocumentDate = new DateOnly(2024, 5, 12),
        Currency = "SEK",
        TotalAmount = 100m,
        ProcessingStatus = ReceiptProcessingStatus.Completed,
        CreatedAtUtc = createdAt,
        UpdatedAtUtc = createdAt
    };

    private static float[] CreateVector(int hotIndex)
    {
        var vector = new float[FoundryEmbeddingOptions.DefaultDimensions];
        vector[hotIndex] = 1;
        return vector;
    }

    private sealed class StubReceiptQueryPlanner : IReceiptQueryPlanner
    {
        public int Calls { get; private set; }

        public ReceiptQueryPlan Plan { get; set; } = new("semantic", null, null, null, "none");

        public Task<ReceiptQueryPlan> PlanAsync(string question, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Plan);
        }
    }

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public string ModelName => "test-embedding-model";

        public int Dimensions => FoundryEmbeddingOptions.DefaultDimensions;

        public float[] VectorToReturn { get; set; } = CreateVector(0);

        public Task<float[]> GenerateAsync(string input, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(VectorToReturn);
        }
    }

    private sealed class StubReceiptAnswerProvider : IReceiptAnswerProvider
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ReceiptQuestionDocument>? Documents { get; private set; }

        public ReceiptAnswerDraft Result { get; set; } = new("No supported answer.", []);

        public Func<IReadOnlyList<ReceiptQuestionDocument>, ReceiptAnswerDraft>? ResultFactory { get; set; }

        public Task<ReceiptAnswerDraft> AnswerAsync(
            string question,
            IReadOnlyList<ReceiptQuestionDocument> documents,
            CancellationToken cancellationToken)
        {
            Calls++;
            Documents = documents;
            return Task.FromResult(ResultFactory?.Invoke(documents) ?? Result);
        }
    }
}