using System.Text.Json.Serialization;

namespace Metriox.SDK.Transport.Contracts;

public sealed class BotEvent
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = default!;

    [JsonPropertyName("platformBotId")]
    public string? PlatformBotId { get; set; }

    [JsonPropertyName("platformUserId")]
    public string? PlatformUserId { get; set; }

    [JsonPropertyName("eventOrigin")]
    public string EventOrigin { get; set; } = default!;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = default!;

    [JsonPropertyName("eventName")]
    public string EventName { get; set; } = default!;

    [JsonPropertyName("eventDate")]
    public DateTimeOffset EventDate { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("propsString")]
    public Dictionary<string, string>? PropsString { get; set; }

    [JsonPropertyName("propsLong")]
    public Dictionary<string, long>? PropsLong { get; set; }

    [JsonPropertyName("propsBool")]
    public Dictionary<string, bool>? PropsBool { get; set; }
}