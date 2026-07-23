using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Metriox.SDK.Tests;

/// <summary>
/// The outgoing-message mapper is what makes a Bot-API bot's own send show up in the transcript at
/// all. The two facts that must not regress: it is marked platform-origin (so the ingest promotes the
/// flat <c>tg.*</c> props into <c>$tg</c>) and it flags <c>tg.from_is_bot</c> (so the transcript sides
/// it as bot → user without the worker-only <c>is_outgoing</c>).
/// </summary>
public class TelegramOutgoingMessageMapperTests
{
    private static Message SentMessage(InlineKeyboardMarkup? kb, string? text = "hi") => new()
    {
        Id = 42,
        Date = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc),
        Text = text,
        Chat = new Chat { Id = 777, Type = ChatType.Private },
        ReplyMarkup = kb,
    };

    [Fact]
    public void Builds_an_outgoing_platform_message_event_with_the_keyboard()
    {
        var kb = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("Buy", "buy") } });

        var ev = TelegramOutgoingMessageMapper.ToBotEvent(SentMessage(kb), "mybot");

        Assert.Equal("platform", ev.EventOrigin); // promotes tg.* -> $tg.*
        Assert.Equal("message", ev.EventType);
        Assert.Equal("tg", ev.Source);
        Assert.Equal("text_message", ev.EventName);
        Assert.Equal("hi", ev.Text);
        Assert.Equal("777", ev.PlatformUserId);

        Assert.True(ev.PropsBool!["tg.from_is_bot"]); // renders as bot -> user
        Assert.Equal(42, ev.PropsLong!["tg.message_id"]);
        Assert.Equal(777, ev.PropsLong!["tg.chat_id"]);
        Assert.Contains("\"d\":\"buy\"", ev.PropsString!["tg.inline_keyboard"]);
    }

    [Fact]
    public void Omits_the_keyboard_prop_when_there_is_none()
    {
        var ev = TelegramOutgoingMessageMapper.ToBotEvent(SentMessage(null), "mybot");

        Assert.False(ev.PropsString!.ContainsKey("tg.inline_keyboard"));
        Assert.True(ev.PropsBool!["tg.from_is_bot"]);
    }

    [Fact]
    public void Event_id_is_stable_per_sent_message()
    {
        var a = TelegramOutgoingMessageMapper.ToBotEvent(SentMessage(null), "mybot");
        var b = TelegramOutgoingMessageMapper.ToBotEvent(SentMessage(null), "mybot");

        Assert.Equal(a.EventId, b.EventId);
    }
}
