using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceManager.Api.Endpoints;

public static class ReceiptEndpoints
{
    public static WebApplication MapReceiptEndpoints(this WebApplication app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Documents");

        documents.MapPost("/ingest", IngestAsync)
            .WithName("IngestReceipt")
            .WithSummary("Upload and register a receipt or invoice")
            .WithDescription("Stores a supported PDF or image, deduplicates by SHA-256, and returns it in Pending state.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ReceiptResponse>(StatusCodes.Status201Created)
            .Produces<ReceiptResponse>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithMetadata(new RequestSizeLimitAttribute(DocumentStorageOptions.MaximumRequestBytes));

        documents.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetReceiptById")
            .WithSummary("Get a receipt by ID")
            .Produces<ReceiptResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Created<ReceiptResponse>, Conflict<ReceiptResponse>, ProblemHttpResult>> IngestAsync(
        IFormFile? file,
        IReceiptIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A file is required.",
                detail: "Submit one supported receipt as the 'file' form field.");
        }

        var result = await ingestionService.IngestAsync(file, cancellationToken);

        return result.Status switch
        {
            ReceiptIngestionStatus.Created => TypedResults.Created(
                $"/api/documents/{result.Receipt!.Id}", result.Receipt),
            ReceiptIngestionStatus.Duplicate => TypedResults.Conflict(result.Receipt!),
            ReceiptIngestionStatus.EmptyFile => TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The uploaded file is empty."),
            ReceiptIngestionStatus.FileTooLarge => TypedResults.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The uploaded file is too large.",
                detail: $"Maximum file size is {DocumentStorageOptions.MaximumFileBytes} bytes."),
            ReceiptIngestionStatus.UnsupportedFileType => TypedResults.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "The file extension is not supported.",
                detail: "Supported extensions are .pdf, .jpg, .jpeg, .png, and .webp."),
            ReceiptIngestionStatus.InvalidFileContents => TypedResults.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "The file contents do not match the declared extension."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<Results<Ok<ReceiptResponse>, NotFound>> GetByIdAsync(
        Guid id,
        IReceiptIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        var receipt = await ingestionService.GetByIdAsync(id, cancellationToken);
        return receipt is null ? TypedResults.NotFound() : TypedResults.Ok(receipt);
    }
}