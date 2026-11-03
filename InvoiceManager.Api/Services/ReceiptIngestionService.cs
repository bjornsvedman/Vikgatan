using System.Security.Cryptography;
using System.Text;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptIngestionService(
    InvoiceDbContext dbContext,
    IOptions<DocumentStorageOptions> storageOptions,
    IWebHostEnvironment environment,
    ILogger<ReceiptIngestionService> logger) : IReceiptIngestionService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    {
        ".pdf",
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _storageRoot = Path.GetFullPath(
        storageOptions.Value.RootPath,
        environment.ContentRootPath);

    public async Task<ReceiptIngestionResult> IngestAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return new(ReceiptIngestionStatus.EmptyFile, null);
        }

        if (file.Length > DocumentStorageOptions.MaximumFileBytes)
        {
            return new(ReceiptIngestionStatus.FileTooLarge, null);
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            return new(ReceiptIngestionStatus.UnsupportedFileType, null);
        }

        if (!await HasValidSignatureAsync(file, extension, cancellationToken))
        {
            return new(ReceiptIngestionStatus.InvalidFileContents, null);
        }

        await using var hashStream = file.OpenReadStream();
        var contentHash = Convert.ToHexString(
            await SHA256.HashDataAsync(hashStream, cancellationToken));

        var existingReceipt = await dbContext.Receipts
            .AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.ContentHash == contentHash, cancellationToken);

        if (existingReceipt is not null)
        {
            return Duplicate(existingReceipt);
        }

        Directory.CreateDirectory(_storageRoot);

        var id = Guid.NewGuid();
        var storedFileName = $"{id:N}{extension}";
        var storedFilePath = Path.Combine(_storageRoot, storedFileName);
        var now = DateTimeOffset.UtcNow;
        var receipt = new Receipt
        {
            Id = id,
            FileName = NormalizeFileName(file.FileName, extension),
            FilePath = storedFileName,
            ContentHash = contentHash,
            ProcessingStatus = ReceiptProcessingStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        try
        {
            await using (var output = new FileStream(
                storedFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            dbContext.Receipts.Add(receipt);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new(ReceiptIngestionStatus.Created, ToResponse(receipt));
        }
        catch (DbUpdateException exception) when (IsContentHashConflict(exception))
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            TryDeleteStoredFile(storedFilePath);

            var concurrentDuplicate = await dbContext.Receipts
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ContentHash == contentHash, cancellationToken);

            if (concurrentDuplicate is null)
            {
                throw;
            }

            return Duplicate(concurrentDuplicate);
        }
        catch
        {
            TryDeleteStoredFile(storedFilePath);
            throw;
        }
    }

    public async Task<ReceiptResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await dbContext.Receipts
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        return receipt is null ? null : ToResponse(receipt);
    }

    private static async Task<bool> HasValidSignatureAsync(
        IFormFile file,
        string extension,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[12];
        var requiredLength = extension switch
        {
            ".pdf" => 5,
            ".jpg" or ".jpeg" => 3,
            ".png" => PngSignature.Length,
            ".webp" => 12,
            _ => 0
        };

        var bytesRead = 0;
        while (bytesRead < requiredLength)
        {
            var read = await stream.ReadAsync(header.AsMemory(bytesRead, requiredLength - bytesRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead < requiredLength)
        {
            return false;
        }

        return extension switch
        {
            ".pdf" => Encoding.ASCII.GetString(header, 0, 5) == "%PDF-",
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
            ".webp" => Encoding.ASCII.GetString(header, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            _ => false
        };
    }

    private static string NormalizeFileName(string fileName, string extension)
    {
        var normalized = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = $"receipt{extension}";
        }

        return normalized.Length <= 255 ? normalized : normalized[..255];
    }

    private static bool IsContentHashConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Receipts_ContentHash"
        };

    private static ReceiptIngestionResult Duplicate(Receipt receipt) =>
        new(ReceiptIngestionStatus.Duplicate, ToResponse(receipt));

    private static ReceiptResponse ToResponse(Receipt receipt) => new(
        receipt.Id,
        receipt.FileName,
        receipt.DocumentDate,
        receipt.Vendor,
        receipt.Currency,
        receipt.TotalAmount,
        receipt.ProcessingStatus,
        receipt.CreatedAtUtc);

    private void TryDeleteStoredFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not remove an incomplete receipt file.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not remove an incomplete receipt file.");
        }
    }
}