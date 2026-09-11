using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot.Types.ReplyMarkups;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Serializes an outgoing message's <see cref="InlineKeyboardMarkup"/> into the compact JSON string
/// Metriox stores at <c>$tg.inline_keyboard</c>. Reporting it lets the per-user conversation view show
/// which buttons a message offered and resolve a pressed callback back to its button label — the same
/// capability the MTProto worker gets for free, brought to Bot-API bots that can only observe their
/// own sends in-process.
///
/// <para>The shape matches the worker's byte-for-byte: a flat, ordered array where a callback button
/// keeps its payload (<c>callback_data</c>) and a url button keeps its target (<c>url</c>); both keep
/// their label (<c>text</c>). Other button kinds (switch-inline, web-app, pay, login, …) carry neither a
/// callback payload nor a url to surface, so they are skipped. Rows are flattened in order.</para>
///
/// <para>The keys are the Bot-API <see cref="InlineKeyboardButton"/> field names, so the value reads as
/// itself in a props inspector. They were single letters (<c>t</c>/<c>d</c>/<c>u</c>) before 2026-07-26 —
/// Metriox still accepts that spelling on read, so an older SDK build keeps working, but new sends should
/// use this one.</para>
/// </summary>
public static class InlineKeyboardSerializer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Returns the compact JSON for <paramref name="markup"/>, or <c>null</c> when there is nothing to
    /// record (no markup, or no callback/url buttons). A <c>null</c> result should simply not be sent.
    /// </summary>
    public static string? ToCompactJson(InlineKeyboardMarkup? markup)
    {
        if (markup?.InlineKeyboard is null)
            return null;

        var buttons = new List<Button>();

        foreach (var row in markup.InlineKeyboard)
        {
            if (row is null)
                continue;

            foreach (var b in row)
            {
                if (b is null)
                    continue;

                // Decoded the same way the callback_query is read back, so the stored key matches the
                // payload it is later looked up by.
                if (!string.IsNullOrEmpty(b.CallbackData))
                    buttons.Add(new Button(b.Text, Data: b.CallbackData));
                else if (!string.IsNullOrEmpty(b.Url))
                    buttons.Add(new Button(b.Text, Url: b.Url));

                // Everything else carries no callback_data or url to surface — not listed.
            }
        }

        return buttons.Count == 0 ? null : JsonSerializer.Serialize(buttons, Json);
    }

    /// <summary>
    /// Label of the button carrying <paramref name="callbackData"/>, or <c>null</c> when the markup does
    /// not offer it.
    ///
    /// <para><b>Why a press should answer this itself.</b> Metriox can reconstruct the label server-side by
    /// matching the payload against the keyboards it has recorded for that message — but a message keeps its
    /// id across an edit, so a bot that navigates by editing one message in place gives that id a whole
    /// sequence of keyboards, and picking the right element means knowing which one was on screen when the
    /// press happened. On the Bot-API path that ordering is not recoverable: the ingest endpoint stamps one
    /// clock reading per HTTP request and copies it to every event in the batch, so a press and the edit it
    /// triggered — milliseconds apart, and usually in the same POST — are indistinguishable in time.</para>
    ///
    /// <para>Telegram removes the need to guess. <c>callback_query.message.reply_markup</c> is the keyboard
    /// exactly as the user saw it, so the answer is a lookup rather than a correlation. Both that field and
    /// <c>callback_query.message</c> are optional — absent for inline-mode buttons and for messages Telegram
    /// no longer treats as accessible — so a <c>null</c> here is normal and simply leaves Metriox to fall
    /// back to its own reconstruction.</para>
    ///
    /// <para>Matching is ordinal and case-sensitive, matching how Metriox compares the stored payload.</para>
    /// </summary>
    public static string? FindLabel(InlineKeyboardMarkup? markup, string? callbackData)
    {
        if (markup?.InlineKeyboard is null || string.IsNullOrEmpty(callbackData))
            return null;

        foreach (var row in markup.InlineKeyboard)
        {
            if (row is null)
                continue;

            foreach (var b in row)
            {
                if (b is null || !string.Equals(b.CallbackData, callbackData, StringComparison.Ordinal))
                    continue;

                // A blank label is nothing to show: leave it null so the transcript falls back to the
                // payload rather than rendering an empty button name.
                return string.IsNullOrWhiteSpace(b.Text) ? null : b.Text;
            }
        }

        return null;
    }

    private sealed record Button(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("callback_data")] string? Data = null,
        [property: JsonPropertyName("url")] string? Url = null);
}
