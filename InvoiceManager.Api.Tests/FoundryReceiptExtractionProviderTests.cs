using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using InvoiceManager.Api.Services;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Api.Tests;

public sealed class FoundryReceiptExtractionProviderTests
{
    [Fact]
    public async Task ExtractAsync_Pdf_UsesResponsesApiAndParsesStructuredMetadata()
    {
        const string output = """
            {"vendor":"Tile House","document_date":"2024-05-12","currency":"SEK","total_amount":125.5,"category":"Flooring","description":"Kitchen tiles","line_items":[],"expense_suggestions":[{"description":"Kitchen tiles","type":"Unclassified","amount":125.5,"evidence":"Tiles purchase line","uncertainty":"Previous material and use are unknown"}]}
            """;
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { output_text = output })
        });
        using var httpClient = new HttpClient(handler);
        var credential = new TestTokenCredential();
        var provider = CreateProvider(httpClient, credential);
        using var document = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 synthetic"));

        var result = await provider.ExtractAsync(
            document,
            "receipt.pdf",
            "application/pdf",
            CancellationToken.None);

        Assert.Equal("Tile House", result.Metadata.Vendor);
        Assert.Equal(125.5m, result.Metadata.TotalAmount);
        Assert.Equal("Unclassified", Assert.Single(result.Metadata.ExpenseSuggestions!).Type);
        Assert.Equal("https://unit-test.services.ai.azure.com/openai/v1/responses", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer test-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("https://ai.azure.com/.default", credential.RequestedScope);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("gpt-5-mini", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        var content = body.RootElement.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_file", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:application/pdf;base64,", content[1].GetProperty("file_data").GetString());
        Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.Contains("Never approve", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExtractAsync_InvalidExpenseSuggestion_RequiresManualFallback()
    {
        const string output = """
            {"line_items":[],"expense_suggestions":[{"description":"Tiles","type":"AutomaticallyApproved","amount":100,"evidence":"Tiles","uncertainty":""}]}
            """;
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { output_text = output })
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, new TestTokenCredential());
        using var document = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 synthetic"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExtractAsync(
            document, "synthetic.pdf", "application/pdf", default));
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedMediaType_RejectsBeforeCallingService()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, new TestTokenCredential());
        using var document = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExtractAsync(document, "receipt.txt", "text/plain", CancellationToken.None));

        Assert.Null(handler.Request);
    }

    private static FoundryReceiptExtractionProvider CreateProvider(
        HttpClient httpClient,
        TokenCredential credential) => new(
            httpClient,
            credential,
            Options.Create(new FoundryExtractionOptions
            {
                Enabled = true,
                Endpoint = "https://unit-test.services.ai.azure.com",
                Deployment = "gpt-5-mini"
            }));

    private sealed class TestTokenCredential : TokenCredential
    {
        public string? RequestedScope { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScope = requestContext.Scopes.Single();
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return response;
        }
    }
}