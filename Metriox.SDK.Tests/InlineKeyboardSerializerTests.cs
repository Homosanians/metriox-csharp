using System.Text.Json;
using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types.ReplyMarkups;

namespace Metriox.SDK.Tests;

/// <summary>
/// The serializer is the whole Bot-API side of the button-label feature: it must produce exactly the
/// compact <c>[{t,d?/u?}]</c> shape Metriox stores at <c>$tg.inline_keyboard</c>, or the transcript
/// silently shows the raw payload instead of the pressed button's label. Pinned here without a bot.
/// </summary>
public class InlineKeyboardSerializerTests
{
    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement;
    }

    [Fact]
    public void Callback_buttons_keep_label_and_payload()
    {
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💰 Partner", "menu_root"),
                InlineKeyboardButton.WithCallbackData("⚙️", "settings"),
            },
        });

        var arr = Parse(InlineKeyboardSerializer.ToCompactJson(markup));

        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("💰 Partner", arr[0].GetProperty("text").GetString());
        Assert.Equal("menu_root", arr[0].GetProperty("callback_data").GetString());
        Assert.False(arr[0].TryGetProperty("u", out _));
        Assert.Equal("settings", arr[1].GetProperty("callback_data").GetString());
    }

    [Fact]
    public void Url_button_keeps_url_not_data()
    {
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("Open", "https://example.com") },
        });

        var arr = Parse(InlineKeyboardSerializer.ToCompactJson(markup));

        Assert.Single(arr.EnumerateArray());
        Assert.Equal("Open", arr[0].GetProperty("text").GetString());
        Assert.Equal("https://example.com", arr[0].GetProperty("url").GetString());
        Assert.False(arr[0].TryGetProperty("d", out _));
    }

    [Fact]
    public void Mixed_callback_and_url_captured_with_their_kind()
    {
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithUrl("Open", "https://example.com"),
                InlineKeyboardButton.WithCallbackData("Press", "cb"),
            },
        });

        var arr = Parse(InlineKeyboardSerializer.ToCompactJson(markup));

        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("https://example.com", arr[0].GetProperty("url").GetString());
        Assert.Equal("cb", arr[1].GetProperty("callback_data").GetString());
    }

    [Fact]
    public void Rows_are_flattened_in_order()
    {
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("A", "a") },
            new[] { InlineKeyboardButton.WithCallbackData("B", "b") },
        });

        var arr = Parse(InlineKeyboardSerializer.ToCompactJson(markup));

        Assert.Equal(new[] { "A", "B" }, arr.EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToArray());
    }

    [Fact]
    public void Buttons_without_callback_or_url_are_skipped()
    {
        // A switch-inline button carries neither a callback payload nor a url to surface.
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithSwitchInlineQuery("Share"),
                InlineKeyboardButton.WithCallbackData("Press", "cb"),
            },
        });

        var arr = Parse(InlineKeyboardSerializer.ToCompactJson(markup));

        Assert.Single(arr.EnumerateArray());
        Assert.Equal("cb", arr[0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public void Null_or_empty_returns_null()
    {
        Assert.Null(InlineKeyboardSerializer.ToCompactJson(null));
        // A keyboard whose only buttons carry nothing surfaceable is nothing to record.
        var onlySwitch = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithSwitchInlineQuery("Share") } });
        Assert.Null(InlineKeyboardSerializer.ToCompactJson(onlySwitch));
    }
}
