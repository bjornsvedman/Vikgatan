using System.Text;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace InvoiceManager.Api.Tests;

public sealed class ReceiptIngestionServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        $"invoice-manager-tests-{Guid.NewGuid():N}");

    private InvoiceDbContext _dbContext = null!;
    private IDbContextTransaction _transaction = null!;
    private ReceiptIngestionService _service = null!;
    private ReceiptReviewService _reviewService = null!;
    private StubReceiptExtractionProvider _extractionProvider = null!;
    private StubEmbeddingProvider _embeddingProvider = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        _dbContext = new InvoiceDbContext(options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _extractionProvider = new StubReceiptExtractionProvider();
        _embeddingProvider = new StubEmbeddingProvider();
        _service = CreateIngestionService(enabled: false, _extractionProvider, _embeddingProvider);
        _reviewService = new ReceiptReviewService(_dbContext);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _dbContext.DisposeAsync();

        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IngestAsync_ValidPdf_CreatesPendingReceiptAndStoresFile()
    {
        var (file, stream) = CreateFile("receipt.pdf", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, result.Status);
            Assert.NotNull(result.Receipt);
            Assert.Equal(ReceiptProcessingStatus.Pending, result.Receipt.Status);
            Assert.Equal(0, _extractionProvider.Calls);

            var storedReceipt = await _dbContext.Receipts.SingleAsync();
            Assert.Equal(result.Receipt.Id, storedReceipt.Id);
            Assert.Equal(64, storedReceipt.ContentHash.Length);
            Assert.True(File.Exists(Path.Combine(_storageRoot, storedReceipt.FilePath)));
        }
    }

    [Fact]
    public async Task GetFileAsync_StoredReceipt_ReturnsFileWithinStorageRoot()
    {
        var (file, stream) = CreateFile("receipt.pdf", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var ingested = await _service.IngestAsync(file, CancellationToken.None);
            var storedFile = await _service.GetFileAsync(ingested.Receipt!.Id, CancellationToken.None);

            Assert.NotNull(storedFile);
            Assert.Equal("application/pdf", storedFile.ContentType);
            Assert.StartsWith(_storageRoot, storedFile.FullPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetFileAsync_PathOutsideStorageRoot_ReturnsNull()
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Pending, DateTimeOffset.UtcNow);
        receipt.FilePath = "..\\outside.pdf";
        _dbContext.Receipts.Add(receipt);
        await _dbContext.SaveChangesAsync();

        var storedFile = await _service.GetFileAsync(receipt.Id, CancellationToken.None);

        Assert.Null(storedFile);
    }

    [Fact]
    public async Task IngestAsync_SameContentTwice_ReturnsExistingReceipt()
    {
        var (firstFile, firstStream) = CreateFile("first.pdf", "%PDF-1.4\nidentical");
        var (secondFile, secondStream) = CreateFile("renamed.pdf", "%PDF-1.4\nidentical");
        using (firstStream)
        using (secondStream)
        {
            var first = await _service.IngestAsync(firstFile, CancellationToken.None);
            var second = await _service.IngestAsync(secondFile, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, first.Status);
            Assert.Equal(ReceiptIngestionStatus.Duplicate, second.Status);
            Assert.Equal(first.Receipt!.Id, second.Receipt!.Id);
            Assert.Single(await _dbContext.Receipts.ToListAsync());
            Assert.Single(Directory.GetFiles(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_UnsupportedExtension_RejectsWithoutWriting()
    {
        var (file, stream) = CreateFile("receipt.txt", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.UnsupportedFileType, result.Status);
            Assert.Empty(await _dbContext.Receipts.ToListAsync());
            Assert.False(Directory.Exists(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_InvalidSignature_RejectsWithoutWriting()
    {
        var (file, stream) = CreateFile("receipt.pdf", "not a PDF");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.InvalidFileContents, result.Status);
            Assert.Empty(await _dbContext.Receipts.ToListAsync());
            Assert.False(Directory.Exists(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_EmptyFile_RejectsWithoutWriting()
    {
        using var stream = new MemoryStream();
        var file = new FormFile(stream, 0, 0, "file", "receipt.pdf");

        var result = await _service.IngestAsync(file, CancellationToken.None);

        Assert.Equal(ReceiptIngestionStatus.EmptyFile, result.Status);
        Assert.Empty(await _dbContext.Receipts.ToListAsync());
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task IngestAsync_FileExceedsLimit_RejectsBeforeReading()
    {
        using var stream = new MemoryStream(
            new byte[checked((int)DocumentStorageOptions.MaximumFileBytes + 1)]);
        var file = new FormFile(stream, 0, stream.Length, "file", "receipt.pdf");

        var result = await _service.IngestAsync(file, CancellationToken.None);

        Assert.Equal(ReceiptIngestionStatus.FileTooLarge, result.Status);
        Assert.Empty(await _dbContext.Receipts.ToListAsync());
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task IngestAsync_ExtractionEnabled_SavesFieldsForHumanReview()
    {
        _extractionProvider.Result = new ReceiptExtractionResult(
            new ExtractedReceipt(
                "Tile House",
                "2024-05-12",
                "sek",
                125.50m,
                "Flooring",
                "Kitchen tile purchase",
                [new ExtractedReceiptLineItem("Ceramic tile", 10, 12.55m, 125.50m, "Flooring")]),
            "{\"vendor\":\"Tile House\"}");
        var service = CreateIngestionService(enabled: true, _extractionProvider, _embeddingProvider);
        var (file, stream) = CreateFile("receipt.pdf", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, result.Status);
            Assert.Equal(ReceiptProcessingStatus.NeedsReview, result.Receipt!.Status);
            Assert.Equal("Tile House", result.Receipt.Vendor);
            Assert.Equal(new DateOnly(2024, 5, 12), result.Receipt.DocumentDate);
            Assert.Equal("SEK", result.Receipt.Currency);
            Assert.Equal(125.50m, result.Receipt.TotalAmount);
            Assert.Equal("Ceramic tile", Assert.Single(result.Receipt.LineItems).Description);
            Assert.Equal("{\"vendor\":\"Tile House\"}", (await _dbContext.Receipts.SingleAsync()).ExtractedMetadataJson);
            var savedReceipt = await _dbContext.Receipts.SingleAsync();
            Assert.NotNull(savedReceipt.Embedding);
            Assert.Equal("stub-embedding-model", savedReceipt.EmbeddingModel);
            Assert.Equal(1, _embeddingProvider.Calls);
            Assert.Equal(1, _extractionProvider.Calls);
        }
    }

    [Fact]
    public async Task IngestAsync_ExtractionFails_KeepsFileAndMarksReceiptFailedForReview()
    {
        _extractionProvider.ExceptionToThrow = new HttpRequestException("synthetic provider failure");
        var service = CreateIngestionService(enabled: true, _extractionProvider, _embeddingProvider);
        var (file, stream) = CreateFile("receipt.pdf", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, result.Status);
            Assert.Equal(ReceiptProcessingStatus.Failed, result.Receipt!.Status);
            Assert.Contains("Review the document", result.Receipt.ProcessingError);
            Assert.True(File.Exists(Path.Combine(_storageRoot, result.Receipt.Id.ToString("N") + ".pdf")));
            Assert.Equal(1, _extractionProvider.Calls);
        }
    }

    [Fact]
    public async Task GetReviewQueueAsync_ReturnsPendingAndNeedsReviewOldestFirst()
    {
        var oldest = CreateReceipt(ReceiptProcessingStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-2));
        var newest = CreateReceipt(ReceiptProcessingStatus.NeedsReview, DateTimeOffset.UtcNow.AddMinutes(-1));
        var completed = CreateReceipt(ReceiptProcessingStatus.Completed, DateTimeOffset.UtcNow);
        _dbContext.Receipts.AddRange(oldest, newest, completed);
        await _dbContext.SaveChangesAsync();

        var queue = await _reviewService.GetReviewQueueAsync(CancellationToken.None);

        Assert.Equal([oldest.Id, newest.Id], queue.Select(receipt => receipt.Id));
    }

    [Fact]
    public async Task ReviewAsync_CompleteReview_SavesMetadataAndLineItems()
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));
        _dbContext.Receipts.Add(receipt);
        await _dbContext.SaveChangesAsync();

        var result = await _reviewService.ReviewAsync(
            receipt.Id,
            new UpdateReceiptReviewRequest
            {
                Vendor = "  Tile House ",
                DocumentDate = new DateOnly(2024, 5, 12),
                Currency = "sek",
                TotalAmount = 125.50m,
                Category = "Flooring",
                Description = "Kitchen floor tiles",
                MarkReviewed = true,
                LineItems =
                [
                    new UpdateReceiptLineItemRequest
                    {
                        Description = "Ceramic tile",
                        Quantity = 10,
                        UnitPrice = 12.55m,
                        LineTotal = 125.50m,
                        Category = "Flooring"
                    }
                ]
            },
            CancellationToken.None);

        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(ReceiptProcessingStatus.Completed, result.Receipt.Status);
        Assert.Equal("Tile House", result.Receipt.Vendor);
        Assert.Equal("SEK", result.Receipt.Currency);
        Assert.Equal("Ceramic tile", Assert.Single(result.Receipt.LineItems).Description);

        var savedReceipt = await _dbContext.Receipts
            .Include(item => item.LineItems)
            .SingleAsync(item => item.Id == receipt.Id);
        Assert.Equal(125.50m, savedReceipt.TotalAmount);
        Assert.Single(savedReceipt.LineItems);
    }

    [Fact]
    public async Task ReviewAsync_ApprovedCategoryCorrection_PreservesApprovalAndInvalidatesEmbedding()
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Completed, DateTimeOffset.UtcNow.AddDays(-1));
        receipt.Vendor = "Tile House";
        receipt.DocumentDate = new DateOnly(2024, 5, 12);
        receipt.Currency = "SEK";
        receipt.TotalAmount = 125.50m;
        receipt.Category = "Paint";
        receipt.Embedding = new Pgvector.Vector(new float[FoundryEmbeddingOptions.DefaultDimensions]);
        receipt.EmbeddingModel = "previous-model";
        receipt.EmbeddingModelVersion = "previous-version";
        var originalPath = receipt.FilePath;
        var originalHash = receipt.ContentHash;
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();

        var result = await _reviewService.ReviewAsync(receipt.Id, new UpdateReceiptReviewRequest
        {
            Vendor = receipt.Vendor,
            DocumentDate = receipt.DocumentDate,
            Currency = receipt.Currency,
            TotalAmount = receipt.TotalAmount,
            Category = "Flooring",
            MarkReviewed = true
        }, CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Receipts.SingleAsync(item => item.Id == receipt.Id);
        var archive = new ReceiptArchiveService(_dbContext);
        var oldCategory = await archive.BrowseAsync(new(Category: "Paint"), CancellationToken.None);
        var newCategory = await archive.BrowseAsync(new(Category: "Flooring"), CancellationToken.None);
        var total = await _dbContext.Receipts.Where(item => item.Category == "Flooring"
            && item.ProcessingStatus == ReceiptProcessingStatus.Completed).SumAsync(item => item.TotalAmount);

        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.Equal(ReceiptProcessingStatus.Completed, saved.ProcessingStatus);
        Assert.Equal("Flooring", saved.Category);
        Assert.Equal(originalPath, saved.FilePath);
        Assert.Equal(originalHash, saved.ContentHash);
        Assert.True(saved.UpdatedAtUtc > saved.CreatedAtUtc);
        Assert.Null(saved.Embedding);
        Assert.Null(saved.EmbeddingModel);
        Assert.Null(saved.EmbeddingModelVersion);
        Assert.Empty(oldCategory.Items);
        Assert.Equal(receipt.Id, Assert.Single(newCategory.Items).Id);
        Assert.Equal(125.50m, total);
    }

    [Fact]
    public async Task ReviewAsync_ApprovedUnchangedContent_KeepsEmbeddingUntilLineItemsChange()
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Completed, DateTimeOffset.UtcNow.AddDays(-1));
        receipt.Vendor = "Tile House";
        receipt.DocumentDate = new DateOnly(2024, 5, 12);
        receipt.Currency = "SEK";
        receipt.TotalAmount = 125.50m;
        receipt.Category = "Flooring";
        receipt.Embedding = new Pgvector.Vector(new float[FoundryEmbeddingOptions.DefaultDimensions]);
        receipt.EmbeddingModel = "previous-model";
        receipt.LineItems.Add(new ReceiptLineItem { Description = "Tiles", Quantity = 10, UnitPrice = 12.55m, LineTotal = 125.50m });
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();
        var request = new UpdateReceiptReviewRequest
        {
            Vendor = " Tile House ",
            DocumentDate = receipt.DocumentDate,
            Currency = "sek",
            TotalAmount = receipt.TotalAmount,
            Category = receipt.Category,
            MarkReviewed = true,
            LineItems = [new() { Description = " Tiles ", Quantity = 10, UnitPrice = 12.55m, LineTotal = 125.50m }]
        };

        await _reviewService.ReviewAsync(receipt.Id, request, CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var unchanged = await _dbContext.Receipts.SingleAsync(item => item.Id == receipt.Id);
        Assert.NotNull(unchanged.Embedding);
        Assert.Equal("previous-model", unchanged.EmbeddingModel);

        var result = await _reviewService.ReviewAsync(receipt.Id, request with
        {
            Description = "Corrected tile purchase",
            TotalAmount = 150m,
            LineItems = [new() { Description = "Corrected tiles", Quantity = 12, UnitPrice = 12.50m, LineTotal = 150m, Category = "Flooring" }]
        }, CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var corrected = await _dbContext.Receipts.Include(item => item.LineItems).SingleAsync(item => item.Id == receipt.Id);

        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.Equal(ReceiptProcessingStatus.Completed, corrected.ProcessingStatus);
        Assert.Equal(150m, corrected.TotalAmount);
        Assert.Equal("Corrected tile purchase", corrected.Description);
        Assert.Equal("Corrected tiles", Assert.Single(corrected.LineItems).Description);
        Assert.Equal(12m, corrected.LineItems[0].Quantity);
        Assert.Null(corrected.Embedding);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("vendor")]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("lineItem")]
    public async Task ReviewAsync_InvalidApprovedEdit_DoesNotChangeStoredReceipt(string invalidField)
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Completed, DateTimeOffset.UtcNow.AddDays(-1));
        receipt.Vendor = "Tile House";
        receipt.DocumentDate = new DateOnly(2024, 5, 12);
        receipt.Currency = "SEK";
        receipt.TotalAmount = 125.50m;
        receipt.Category = "Flooring";
        receipt.Embedding = new Pgvector.Vector(new float[FoundryEmbeddingOptions.DefaultDimensions]);
        _dbContext.Add(receipt);
        await _dbContext.SaveChangesAsync();
        var request = new UpdateReceiptReviewRequest
        {
            Vendor = receipt.Vendor,
            DocumentDate = receipt.DocumentDate,
            Currency = receipt.Currency,
            TotalAmount = receipt.TotalAmount,
            Category = receipt.Category,
            MarkReviewed = true
        };
        request = invalidField switch
        {
            "category" => request with { Category = new string('x', 129) },
            "vendor" => request with { Vendor = null },
            "amount" => request with { TotalAmount = -1 },
            "date" => request with { DocumentDate = null },
            _ => request with { LineItems = [new() { Description = "" }] }
        };

        var result = await _reviewService.ReviewAsync(receipt.Id, request, CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Receipts.SingleAsync(item => item.Id == receipt.Id);

        Assert.Equal(ReceiptReviewStatus.Invalid, result.Status);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Equal(ReceiptProcessingStatus.Completed, saved.ProcessingStatus);
        Assert.Equal("Tile House", saved.Vendor);
        Assert.Equal("Flooring", saved.Category);
        Assert.Equal(125.50m, saved.TotalAmount);
        Assert.NotNull(saved.Embedding);
        Assert.Equal(saved.CreatedAtUtc, saved.UpdatedAtUtc);
    }

    [Fact]
    public async Task ReviewAsync_IncompleteCompletion_ReturnsValidationErrorsWithoutChangingReceipt()
    {
        var receipt = CreateReceipt(ReceiptProcessingStatus.Pending, DateTimeOffset.UtcNow);
        _dbContext.Receipts.Add(receipt);
        await _dbContext.SaveChangesAsync();

        var result = await _reviewService.ReviewAsync(
            receipt.Id,
            new UpdateReceiptReviewRequest { MarkReviewed = true },
            CancellationToken.None);

        Assert.Equal(ReceiptReviewStatus.Invalid, result.Status);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Equal(ReceiptProcessingStatus.Pending, (await _dbContext.Receipts.SingleAsync()).ProcessingStatus);
    }

    [Fact]
    public async Task ReviewAsync_UnknownReceipt_ReturnsNotFound()
    {
        var result = await _reviewService.ReviewAsync(
            Guid.NewGuid(),
            new UpdateReceiptReviewRequest(),
            CancellationToken.None);

        Assert.Equal(ReceiptReviewStatus.NotFound, result.Status);
    }

    private static Receipt CreateReceipt(ReceiptProcessingStatus status, DateTimeOffset createdAt) => new()
    {
        FileName = "receipt.pdf",
        FilePath = $"{Guid.NewGuid():N}.pdf",
        ContentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        ProcessingStatus = status,
        CreatedAtUtc = createdAt,
        UpdatedAtUtc = createdAt
    };

    private ReceiptIngestionService CreateIngestionService(
        bool enabled,
        IReceiptExtractionProvider extractionProvider,
        IEmbeddingProvider embeddingProvider) => new(
            _dbContext,
            Options.Create(new DocumentStorageOptions { RootPath = _storageRoot }),
            Options.Create(new FoundryExtractionOptions { Enabled = enabled }),
            extractionProvider,
            embeddingProvider,
            new TestWebHostEnvironment { ContentRootPath = _storageRoot },
            NullLogger<ReceiptIngestionService>.Instance);

    private static (FormFile File, MemoryStream Stream) CreateFile(string fileName, string contents)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(contents));
        return (new FormFile(stream, 0, stream.Length, "file", fileName), stream);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "InvoiceManager.Api.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Testing";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("invoice_manager_tests")
        .WithUsername("invoice_manager")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        await using var dbContext = new InvoiceDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}

public sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public int Calls { get; private set; }

    public string ModelName => "stub-embedding-model";

    public int Dimensions => FoundryEmbeddingOptions.DefaultDimensions;

    public Task<float[]> GenerateAsync(string input, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new float[Dimensions]);
    }
}

public sealed class StubReceiptExtractionProvider : IReceiptExtractionProvider
{
    public int Calls { get; private set; }

    public Exception? ExceptionToThrow { get; set; }

    public ReceiptExtractionResult Result { get; set; } = new(
        new ExtractedReceipt(null, null, null, null, null, null, []),
        "{}");

    public Task<ReceiptExtractionResult> ExtractAsync(
        Stream fileStream,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        Calls++;
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }
}