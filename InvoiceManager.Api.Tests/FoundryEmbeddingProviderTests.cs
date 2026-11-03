using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using InvoiceManager.Api.Services;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Api.Tests;

public sealed class FoundryEmbeddingProviderTests
{
    [Fact]
    public async Task GenerateAsync_UsesConfiguredDeploymentAndReturnsEmbedding()
    {
        var values = Enumerable.Range(0, 1536).Select(index => (float)index).ToArray();
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { embedding = values } } })
        });
        using var httpClient = new HttpClient(handler);
        var credential = new TestTokenCredential();
        var provider = CreateProvider(httpClient, credential);

        var result = await provider.GenerateAsync("synthetic receipt text", CancellationToken.None);

        Assert.Equal(1536, result.Length);
        Assert.Equal("text-embedding-3-small", provider.ModelName);
        Assert.Equal("https://unit-test.services.ai.azure.com/openai/v1/embeddings", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer test-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("https://cognitiveservices.azure.com/.default", credential.RequestedScope);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("synthetic receipt text", body.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task GenerateAsync_WrongDimension_RejectsResponse()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { embedding = new[] { 0.1f, 0.2f } } } })
        });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, new TestTokenCredential());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GenerateAsync("synthetic receipt text", CancellationToken.None));

        Assert.Contains("returned 2 dimensions", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_EmptyInput_RejectsBeforeCallingService()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, new TestTokenCredential());

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateAsync(" ", CancellationToken.None));

        Assert.Null(handler.Request);
    }

    private static FoundryEmbeddingProvider CreateProvider(
        HttpClient httpClient,
        TokenCredential credential) => new(
            httpClient,
            credential,
            Options.Create(new FoundryEmbeddingOptions
            {
                Endpoint = "https://unit-test.services.ai.azure.com",
                Deployment = "text-embedding-3-small",
                Dimensions = 1536
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