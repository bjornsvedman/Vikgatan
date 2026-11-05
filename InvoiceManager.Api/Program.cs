using Azure.Core;
using Azure.Identity;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Endpoints;
using InvoiceManager.Api.Middleware;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadHttpRequestExceptionHandler>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.ConfigureHttpJsonOptions(options =>
	options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FormOptions>(options =>
	options.MultipartBodyLengthLimit = DocumentStorageOptions.MaximumRequestBytes);
builder.Services.Configure<DocumentStorageOptions>(
	builder.Configuration.GetSection(DocumentStorageOptions.SectionName));
builder.Services.Configure<FoundryEmbeddingOptions>(
	builder.Configuration.GetSection(FoundryEmbeddingOptions.SectionName));
builder.Services.Configure<FoundryExtractionOptions>(
	builder.Configuration.GetSection(FoundryExtractionOptions.SectionName));
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddHttpClient<IEmbeddingProvider, FoundryEmbeddingProvider>();
builder.Services.AddHttpClient<IReceiptExtractionProvider, FoundryReceiptExtractionProvider>();
builder.Services.AddHttpClient<IReceiptAnswerProvider, FoundryReceiptAnswerProvider>();
builder.Services.AddHttpClient<IReceiptQueryPlanner, FoundryReceiptQueryPlanner>();

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
	throw new InvalidOperationException(
		"Set the 'ConnectionStrings:Postgres' connection string with .NET User Secrets or environment variables.");
}

builder.Services.AddDbContext<InvoiceDbContext>(options =>
	options.UseNpgsql(connectionString, postgres => postgres.UseVector()));
builder.Services.AddScoped<IReceiptIngestionService, ReceiptIngestionService>();
builder.Services.AddScoped<IReceiptReviewService, ReceiptReviewService>();
builder.Services.AddScoped<ReceiptArchiveService>();
builder.Services.AddScoped<PropertyService>();
builder.Services.AddScoped<ImprovementService>();
builder.Services.AddScoped<ExpenseReportService>();
builder.Services.AddScoped<IReceiptQuestionService, ReceiptQuestionService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAntiforgery();

app.MapGet("/", () => Results.Ok(new { name = "Invoice Manager API", status = "running" }));
app.MapGet("/api/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
{
	context.Response.Headers.CacheControl = "no-store";
	var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
	return TypedResults.Ok(new AntiforgeryTokenResponse(token));
})
	.WithName("GetAntiforgeryToken")
	.WithSummary("Get a request token for browser form submissions")
	.Produces<AntiforgeryTokenResponse>();

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.MapReceiptEndpoints();
app.MapPropertyEndpoints();
app.MapImprovementEndpoints();

app.Run();
