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
    public async Task QueryAsync_NoEmbeddings_UsesRecentReceiptsWithoutCallingEmbeddingProvider()
    {
        var firstReceipt = CreateReceipt("Tile House", DateTimeOffset.UtcNow.AddMinutes(-2));
        var secondReceipt = CreateReceipt("Paint Store", DateTimeOffset.UtcNow.AddMinutes(-1));
        _dbContext.Receipts.AddRange(firstReceipt, secondReceipt);
        await _dbContext.SaveChangesAsync();
        _answerProvider.Result = new ReceiptAnswerDraft("The latest receipt is from Paint Store.", [secondReceipt.Id]);

        var result = await _service.QueryAsync("Which receipt is newest?", CancellationToken.None);

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

        var result = await _service.QueryAsync("Which receipt mentions floor tiles?", CancellationToken.None);

        Assert.Equal(1, _embeddingProvider.Calls);
        Assert.Equal(matchingReceipt.Id, Assert.Single(result.Sources).Id);
        Assert.Contains("Flooring Shop", result.Answer);
    }

    [Fact]
    public async Task QueryAsync_NoReceipts_ReturnsEmptyStateWithoutCallingProviders()
    {
        var result = await _service.QueryAsync("How much did I spend?", CancellationToken.None);

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

        var result = await _service.QueryAsync("What did I buy?", CancellationToken.None);

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

        _dbContext.Receipts.AddRange(
            firstTileReceipt,
            secondTileReceipt,
            euroTileReceipt,
            previousYearReceipt,
            otherCategoryReceipt,
            unreviewedReceipt);
        await _dbContext.SaveChangesAsync();
        _queryPlanner.Plan = new ReceiptQueryPlan("sum_total", 2024, "Tile House", "Flooring", "none");

        var result = await _service.QueryAsync(
            "How much did I spend at Tile House on flooring in 2024?",
            CancellationToken.None);

        Assert.Contains("SEK: 125.50 (2 reviewed receipts)", result.Answer);
        Assert.Contains("EUR: 10.00 (1 reviewed receipt)", result.Answer);
        Assert.Equal(3, result.TotalSourceCount);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(0, _embeddingProvider.Calls);
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

        var result = await _service.QueryAsync("Show spending by year", CancellationToken.None);

        Assert.Contains("2023 / SEK: 100.00 (1 reviewed receipt)", result.Answer);
        Assert.Contains("2024 / SEK: 175.25 (1 reviewed receipt)", result.Answer);
        Assert.Equal(2, result.TotalSourceCount);
        Assert.Equal(0, _embeddingProvider.Calls);
        Assert.Equal(0, _answerProvider.Calls);
    }

    private static Receipt CreateReceipt(string vendor, DateTimeOffset createdAt) => new()
    {
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