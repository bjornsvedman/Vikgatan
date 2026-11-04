using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Api.Services;

public sealed class FoundryReceiptQueryPlanner(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<FoundryExtractionOptions> options) : IReceiptQueryPlanner
{
    private const string QueryPlanSchema = """
        {
          "type": "object",
          "properties": {
            "query_type": { "type": "string", "enum": ["semantic", "sum_total"] },
            "year": { "type": ["integer", "null"] },
            "vendor": { "type": ["string", "null"] },
            "category": { "type": ["string", "null"] },
            "group_by": { "type": "string", "enum": ["none", "year", "vendor", "category"] }
          },
          "required": ["query_type", "year", "vendor", "category", "group_by"],
          "additionalProperties": false
        }
        """;

    private const string Instructions = """
        Classify the user's receipt question. Use sum_total for exact spending, amount, or total-cost
        questions; use group_by=year, vendor, or category when a breakdown is requested, otherwise group_by=none.
        Populate year/vendor/category only when the user explicitly specifies them. Use semantic for descriptive
        or item-discovery questions. Never calculate an amount and never return SQL.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FoundryExtractionOptions _options = options.Value;

    public async Task<ReceiptQueryPlan> PlanAsync(string question, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Configure Foundry:Extraction:Endpoint as an HTTPS resource endpoint.");
        }

        if (string.IsNullOrWhiteSpace(_options.Deployment) || string.IsNullOrWhiteSpace(_options.TokenScope))
        {
            throw new InvalidOperationException("Configure the Foundry extraction deployment and Entra token scope before planning queries.");
        }

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([_options.TokenScope]),
            cancellationToken);
        var schema = JsonSerializer.Deserialize<JsonElement>(QueryPlanSchema);
        var requestBody = new
        {
            model = _options.Deployment,
            store = false,
            input = new[]
            {
                new { role = "system", content = Instructions },
                new { role = "user", content = question }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "receipt_query_plan",
                    strict = true,
                    schema
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint.ToString().TrimEnd('/') + "/openai/v1/responses"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry query planning failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var responseJson = JsonDocument.Parse(responseBody);
        var outputText = ReadOutputText(responseJson.RootElement);
        return JsonSerializer.Deserialize<ReceiptQueryPlan>(outputText, JsonOptions)
            ?? throw new InvalidOperationException("Foundry returned an empty query plan.");
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString()!;
        }

        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type)
                        && type.GetString() == "output_text"
                        && part.TryGetProperty("text", out var text))
                    {
                        return text.GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException("Foundry returned no structured query plan.");
    }
}