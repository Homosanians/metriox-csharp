using Metriox.SDK.Transport.Contracts;

namespace Metriox.SDK;

public sealed class TgCustomBotEventBuilder
{
    private readonly BotEvent _e = new();

    private readonly Dictionary<string, string> _propsString = new();
    private readonly Dictionary<string, long> _propsLong = new();
    private readonly Dictionary<string, bool> _propsBool = new();

    private TgCustomBotEventBuilder()
    {
        _e.Source = "tg";
        _e.EventOrigin = "custom";
        _e.EventDate = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Create a custom (user-defined) event for Telegram source.
    /// </summary>
    public static TgCustomBotEventBuilder Create(string eventType, string eventName)
        => new TgCustomBotEventBuilder()
            .WithEventType(eventType)
            .WithEventName(eventName);

    public TgCustomBotEventBuilder WithEventId(Guid id)
    {
        _e.EventId = id;
        return this;
    }

    public TgCustomBotEventBuilder WithNewEventId()
    {
        _e.EventId = Guid.NewGuid();
        return this;
    }

    public TgCustomBotEventBuilder WithPlatformBotId(string? id)
    {
        _e.PlatformBotId = id;
        return this;
    }

    public TgCustomBotEventBuilder WithPlatformUserId(string? id)
    {
        _e.PlatformUserId = id;
        return this;
    }

    public TgCustomBotEventBuilder WithEventDate(DateTimeOffset dt)
    {
        _e.EventDate = dt;
        return this;
    }

    public TgCustomBotEventBuilder WithText(string? text)
    {
        _e.Text = text;
        return this;
    }

    // ---- props ----

    public TgCustomBotEventBuilder AddProp(string key, string value)
    {
        _propsString[key] = value;
        return this;
    }

    public TgCustomBotEventBuilder AddProp(string key, long value)
    {
        _propsLong[key] = value;
        return this;
    }

    public TgCustomBotEventBuilder AddProp(string key, bool value)
    {
        _propsBool[key] = value;
        return this;
    }

    public TgCustomBotEventBuilder AddProps(IDictionary<string, string> values)
    {
        foreach (var kv in values) _propsString[kv.Key] = kv.Value;
        return this;
    }

    public TgCustomBotEventBuilder AddProps(IDictionary<string, long> values)
    {
        foreach (var kv in values) _propsLong[kv.Key] = kv.Value;
        return this;
    }

    public TgCustomBotEventBuilder AddProps(IDictionary<string, bool> values)
    {
        foreach (var kv in values) _propsBool[kv.Key] = kv.Value;
        return this;
    }

    public BotEvent Build()
    {
        if (_e.EventId == Guid.Empty)
            _e.EventId = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(_e.EventType))
            throw new InvalidOperationException("EventType is required.");

        if (string.IsNullOrWhiteSpace(_e.EventName))
            throw new InvalidOperationException("EventName is required.");
        
        _e.Source = "telegram";
        _e.EventOrigin = "custom";

        _e.PropsString = _propsString.Count > 0 ? _propsString : null;
        _e.PropsLong   = _propsLong.Count > 0   ? _propsLong   : null;
        _e.PropsBool   = _propsBool.Count > 0   ? _propsBool   : null;

        return _e;
    }

    private TgCustomBotEventBuilder WithEventType(string type)
    {
        _e.EventType = type;
        return this;
    }

    private TgCustomBotEventBuilder WithEventName(string name)
    {
        _e.EventName = name;
        return this;
    }
}
