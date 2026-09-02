using System.Text.Json.Serialization;

namespace Metriox.SDK.Transport.Contracts;

public sealed class BotEventsRequest
{
    [JsonPropertyName("events")]
    public List<BotEvent> Events { get; init; } = new();

    /// <summary>
    /// Who the people in <see cref="Events"/> are, one entry per distinct user in the batch.
    ///
    /// <para>Omitted when nothing in the batch disclosed a user. Metriox has always accepted this
    /// field — the Mini App SDK fills it — but no release of this SDK ever did, which is why a Bot-API
    /// bot's users showed up in the dashboard as bare numeric ids: the events carried the id, and
    /// nothing carried the name. See <see cref="BotUserSnapshot"/>.</para>
    /// </summary>
    [JsonPropertyName("users")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BotUserSnapshot>? Users { get; init; }
}
