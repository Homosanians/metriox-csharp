using Metriox.SDK.Transport.Contracts;
using System.Text;
using System.Text.Json;

namespace Metriox.SDK.Transport;

public class HttpTransport : ITransport
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly static JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Uri _ingestEndpoint;
    private readonly Uri _ingestTelegramEndpoint;
    
    public HttpTransport(HttpClient http, string apiKey, Uri? ingestEndpoint = null)
    {
        _http = http;
        _apiKey = apiKey;

        _ingestEndpoint = ingestEndpoint != null ? ingestEndpoint : new Uri($"https://ingest.metriox.com");
        
        _ingestTelegramEndpoint = new Uri(_ingestEndpoint, "/tg");
    }
    
    public async Task<BotEventsResponse> SendTelegram(BotEventsRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOpts);

        using var msg = new HttpRequestMessage(HttpMethod.Post, _ingestTelegramEndpoint);
        msg.Headers.Add("X-API-Key", _apiKey);
        msg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(msg, ct);

        resp.EnsureSuccessStatusCode();

        return new BotEventsResponse()
        {
            // Nothing significant important yet for jan 2026
        };
    }
}
