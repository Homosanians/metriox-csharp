using System.Text;
using System.Text.Json;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Serializes a message's formatting spans into the canonical <c>tg.entities</c> wire shape:
/// <c>[{ "type": …, "offset": …, "length": …, "url": …? }]</c>, one compact JSON string.
///
/// <para>The keys are the Bot-API <see cref="MessageEntity"/> field names. They were single letters
/// (<c>t</c>/<c>o</c>/<c>l</c>/<c>u</c>) before 2026-07-26 — Metriox still accepts that spelling on read,
/// so an older SDK build keeps working, but new sends should use this one.</para>
///
/// <para>Telegram never sends formatted text — it sends plain text plus these offset/length spans — so
/// without them Metriox cannot render a bold word or an inline link at all, only count that some
/// existed (<c>tg.entities_count</c>). Offsets are UTF-16 code units, which is what Telegram means and
/// what C# and JavaScript both index by, so no conversion happens anywhere in the chain.</para>
///
/// <para>Type tokens are the Bot-API snake_case names (<c>text_link</c>, <c>bot_command</c>,
/// <c>phone_number</c>, …) rather than the SDK's PascalCase enum, because the Bot-API vocabulary is the
/// canon that Metriox's MTProto worker also maps onto. An enum value this SDK build does not know still
/// serializes — its name is lowercased — so a Telegram addition does not need an SDK release.</para>
/// </summary>
public static class MessageEntitySerializer
{
    /// <summary>
    /// Matches the backend cap. A long post can carry hundreds of spans (every @mention is one) and past
    /// a few dozen the transcript renders identically while the stored row grows without bound.
    /// </summary>
    public const int MaxSpans = 64;

    /// <summary>
    /// Compact JSON for <paramref name="entities"/>, or <c>null</c> when there is nothing to send — so an
    /// unformatted message carries no entities prop at all.
    /// </summary>
    public static string? ToCompactJson(MessageEntity[]? entities)
    {
        if (entities is null || entities.Length == 0) return null;

        using var buffer = new MemoryStream();
        var wrote = false;

        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();

            var count = 0;
            foreach (var e in entities)
            {
                if (e is null || e.Length <= 0 || e.Offset < 0) continue;
                if (count == MaxSpans) break;

                w.WriteStartObject();
                w.WriteString("type", ToToken(e.Type));
                w.WriteNumber("offset", e.Offset);
                w.WriteNumber("length", e.Length);
                // Only text_link carries its own target; everything else styles the text in place.
                if (!string.IsNullOrEmpty(e.Url)) w.WriteString("url", e.Url);
                w.WriteEndObject();

                count++;
                wrote = true;
            }

            w.WriteEndArray();
        }

        return wrote ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
    }

    // PascalCase enum -> Bot-API snake_case token. Only the multi-word and renamed ones need listing;
    // single-word values (Bold, Italic, Code, Pre, Url, Mention, Hashtag, Cashtag, Email, Spoiler,
    // Blockquote, Underline) are correct once lowercased.
    private static string ToToken(MessageEntityType type) => type switch
    {
        MessageEntityType.TextLink => "text_link",
        MessageEntityType.TextMention => "text_mention",
        MessageEntityType.BotCommand => "bot_command",
        MessageEntityType.PhoneNumber => "phone_number",
        MessageEntityType.CustomEmoji => "custom_emoji",
        MessageEntityType.Strikethrough => "strikethrough",
        _ => type.ToString().ToLowerInvariant()
    };
}
