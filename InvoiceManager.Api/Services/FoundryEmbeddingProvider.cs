using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Api.Services;

public sealed class FoundryEmbeddingProvider(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<FoundryEmbeddingOptions> options) : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FoundryEmbeddingOptions _options = options.Value;

    public string ModelName => _options.Deployment;

    public int Dimensions => _options.Dimensions;

    public async Task<float[]> GenerateAsync(string input, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Configure Foundry:Embeddings:Endpoint as an HTTPS resource endpoint.");
        }

        if (string.IsNullOrWhiteSpace(_options.Deployment))
        {
            throw new InvalidOperationException("Configure Foundry:Embeddings:Deployment with the embedding deployment name.");
        }

        if (_options.Dimensions <= 0)
        {
            throw new InvalidOperationException("Foundry embedding dimensions must be positive.");
        }

        if (string.IsNullOrWhiteSpace(_options.TokenScope))
        {
            throw new InvalidOperationException("Configure Foundry:Embeddings:TokenScope for Entra authentication.");
        }

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([_options.TokenScope]),
            cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint.ToString().TrimEnd('/') + "/openai/v1/embeddings"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(
            new EmbeddingRequest(_options.Deployment, input),
            options: JsonOptions);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry embedding request failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(
            JsonOptions,
            cancellationToken);
        var embedding = result?.Data?.FirstOrDefault()?.Embedding;

        if (embedding is null)
        {
            throw new InvalidOperationException("Foundry returned no embedding vector.");
        }

        if (embedding.Length != _options.Dimensions)
        {
            throw new InvalidOperationException(
                $"Foundry returned {embedding.Length} dimensions; the configured vector column requires {_options.Dimensions}.");
        }

        return embedding;
    }

    private sealed record EmbeddingRequest(string Model, string Input);

    private sealed class EmbeddingResponse
    {
        public List<EmbeddingData>? Data { get; init; }
    }

    private sealed class EmbeddingData
    {
        public float[]? Embedding { get; init; }
    }
}