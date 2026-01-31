using System.Text.Json.Serialization;

namespace Metriox.SDK.Transport.Contracts;

public sealed class BotEventsRequest
{
    [JsonPropertyName("events")]
    public List<BotEvent> Events { get; init; } = new();
}
