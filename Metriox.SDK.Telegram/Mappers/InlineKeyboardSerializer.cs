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
/// keeps its payload (<c>d</c>) and a url button keeps its target (<c>u</c>); both keep their label
/// (<c>t</c>). Other button kinds (switch-inline, web-app, pay, login, …) carry neither a callback
/// payload nor a url to surface, so they are skipped. Rows are flattened in order.</para>
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

    private sealed record Button(
        [property: JsonPropertyName("t")] string Text,
        [property: JsonPropertyName("d")] string? Data = null,
        [property: JsonPropertyName("u")] string? Url = null);
}
