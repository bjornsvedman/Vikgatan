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

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        _dbContext = new InvoiceDbContext(options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _service = new ReceiptIngestionService(
            _dbContext,
            Options.Create(new DocumentStorageOptions { RootPath = _storageRoot }),
            new TestWebHostEnvironment { ContentRootPath = _storageRoot },
            NullLogger<ReceiptIngestionService>.Instance);
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