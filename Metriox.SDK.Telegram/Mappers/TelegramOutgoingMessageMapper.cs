using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Metriox.SDK.Transport.Contracts;
using Telegram.Bot.Types;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Builds the <see cref="BotEvent"/> for a message the bot just sent, so its inline keyboard reaches
/// Metriox. The Bot API never reports a bot's own sends back to the server, so — unlike the MTProto
/// worker — an SDK bot is the only thing that can observe them; call this with the <see cref="Message"/>
/// returned by <c>SendMessage</c> and enqueue the result.
///
/// <para>The event is marked <c>EventOrigin = "platform"</c> so the ingest promotes its flat
/// <c>tg.*</c> props into the canonical <c>$tg</c> section (same path as incoming updates), and
/// <c>tg.from_is_bot = true</c> so the transcript renders it as an outgoing (bot → user) bubble
/// without relying on the worker-only <c>is_outgoing</c> flag. The offered keyboard rides in
/// <c>tg.inline_keyboard</c> via <see cref="InlineKeyboardSerializer"/>.</para>
///
/// <para>Best suited to private chats, where the conversation is one-to-one and the chat id is the
/// user id — so the outgoing bubble lines up with that user's incoming messages.</para>
/// </summary>
public static class TelegramOutgoingMessageMapper
{
    /// <param name="sent">The message returned by <c>SendMessage</c> (carries the echoed reply_markup).</param>
    /// <param name="platformBotId">Your bot identifier (username or numeric id as string).</param>
    /// <param name="receivedAtUtc">Overrides the event timestamp; defaults to the message date, else now.</param>
    public static BotEvent ToBotEvent(Message sent, string platformBotId, DateTimeOffset? receivedAtUtc = null)
    {
        if (sent is null) throw new ArgumentNullException(nameof(sent));
        if (string.IsNullOrEmpty(platformBotId)) throw new ArgumentException("platformBotId is required.", nameof(platformBotId));

        var now = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var eventDate = sent.Date != default ? sent.Date : now;

        var text = sent.Text ?? sent.Caption;

        var propsString = new Dictionary<string, string>(capacity: 4)
        {
            ["tg.chat_type"] = sent.Chat.Type.ToString()
        };

        var propsLong = new Dictionary<string, long>(capacity: 2)
        {
            ["tg.message_id"] = sent.Id,
            ["tg.chat_id"] = sent.Chat.Id
        };

        // The canonical, producer-independent answer to "who sent this". The server writes the same
        // field from the MTProto worker's is_outgoing flag, so a transcript no longer has to know which
        // producer captured the row to lay the bubble out.
        propsString["tg.direction"] = "outbound";

        // Still sent for older servers, whose ConversationClassifier reads this rather than tg.direction.
        var propsBool = new Dictionary<string, bool>(capacity: 1)
        {
            ["tg.from_is_bot"] = true
        };

        var keyboard = InlineKeyboardSerializer.ToCompactJson(sent.ReplyMarkup);
        if (keyboard is not null)
            propsString["tg.inline_keyboard"] = keyboard;

        // Formatting spans of the bot's own message, so its bold text and links render in the transcript
        // rather than arriving as flat text.
        var entities = MessageEntitySerializer.ToCompactJson(sent.Entities ?? sent.CaptionEntities);
        if (entities is not null)
            propsString["tg.entities"] = entities;

        return new BotEvent
        {
            // Natural Telegram coordinates, matching what Metriox's MTProto worker derives for the same
            // message -- so if both capture it, it is ONE event. An edit is keyed by its edit time so it
            // does not collapse onto the original send.
            EventId = TelegramEventIdentity.Uuid5(
                sent.EditDate is { } editedAt
                    ? TelegramEventIdentity.MessageEdit(sent.Chat.Id, sent.Id, TelegramEventIdentity.ToUnixSeconds(editedAt))
                    : TelegramEventIdentity.Message(sent.Chat.Id, sent.Id)),
            Source = "tg",
            PlatformBotId = platformBotId,
            // In a private chat the chat id is the user id, so the outgoing bubble aligns with that
            // user's incoming messages.
            PlatformUserId = sent.Chat.Id.ToString(CultureInfo.InvariantCulture),
            EventOrigin = "platform",
            EventType = "message",
            EventName = text is not null ? "text_message" : "message",
            EventDate = eventDate,
            Text = text,
            PropsString = propsString,
            PropsLong = propsLong,
            PropsBool = propsBool
        };
    }
}
