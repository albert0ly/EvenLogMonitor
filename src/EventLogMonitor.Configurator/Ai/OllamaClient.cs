using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EventLogMonitor.Configurator.Ai;

public sealed record ChatTurn(string Role, string Content);

/// <summary>Minimal client for the local Ollama HTTP API (no NuGet dependency).</summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public OllamaClient(AiSettings settings)
    {
        _settings = settings;
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds))
        };
    }

    /// <summary>Returns null when reachable and the model is installed; otherwise a human-readable problem.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", ct);
            if (!response.IsSuccessStatusCode) return $"Ollama answered HTTP {(int)response.StatusCode}.";
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var names = (json?["models"] as JsonArray)?
                .Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? [];
            var wanted = _settings.Model;
            var found = names.Any(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(n, wanted + ":latest", StringComparison.OrdinalIgnoreCase));
            return found ? null : $"Model '{wanted}' is not installed. Run:  ollama pull {wanted}   (installed: {(names.Count == 0 ? "none" : string.Join(", ", names))})";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Cannot reach Ollama at {_settings.Endpoint}. Is it installed and running? ({ex.Message})";
        }
    }

    /// <summary>Sends a chat request constrained to a JSON schema and returns the raw JSON text of the reply.</summary>
    public async Task<string> ChatJsonAsync(IReadOnlyList<ChatTurn> messages, JsonNode schema, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _settings.Model,
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["messages"] = new JsonArray(messages.Select(m =>
                (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = 8192 }
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("api/chat", body, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string detail;
                try { detail = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text; } catch { detail = text; }
                throw new InvalidOperationException($"Ollama error (HTTP {(int)response.StatusCode}): {detail}");
            }
            var content = JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(content)
                ? throw new InvalidOperationException("The model returned an empty answer.") : content;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The model did not answer within {_settings.TimeoutSeconds} seconds. " +
                                       "The first request after start-up can be slow while the model loads; try again or use a smaller model.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Cannot reach Ollama at {_settings.Endpoint}: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
