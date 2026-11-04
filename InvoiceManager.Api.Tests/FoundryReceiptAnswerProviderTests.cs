using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using InvoiceManager.Api.Services;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Api.Tests;

public sealed class FoundryReceiptAnswerProviderTests
{
    [Fact]
    public async Task AnswerAsync_UsesResponsesApiAndParsesCitations()
    {
        var receiptId = Guid.NewGuid();
        var output = JsonSerializer.Serialize(new
        {
            answer = "The tile receipt is from Tile House.",
            source_ids = new[] { receiptId }
        });
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { output_text = output })
        });
        using var httpClient = new HttpClient(handler);
        var credential = new TestTokenCredential();
        var provider = new FoundryReceiptAnswerProvider(
            httpClient,
            credential,
            Options.Create(new FoundryExtractionOptions
            {
                Endpoint = "https://unit-test.services.ai.azure.com",
                Deployment = "gpt-5-mini"
            }));
        var result = await provider.AnswerAsync(
            "Which receipt is for tile?",
            [new ReceiptQuestionDocument(
                receiptId,
                "tile.pdf",
                "Tile House",
                new DateOnly(2024, 5, 12),
                "SEK",
                125.50m,
                "Flooring",
                "Kitchen tiles",
                "Completed",
                [])],
            CancellationToken.None);

        Assert.Equal("The tile receipt is from Tile House.", result.Answer);
        Assert.Equal(receiptId, Assert.Single(result.SourceIds));
        Assert.Equal("https://unit-test.services.ai.azure.com/openai/v1/responses", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer test-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("https://ai.azure.com/.default", credential.RequestedScope);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("gpt-5-mini", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.True(handler.RequestBody!.Contains("Which receipt is for tile?", StringComparison.Ordinal));
    }

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
