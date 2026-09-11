using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Metriox.SDK.Tests;

/// <summary>
/// Reporting the pressed button's label from the press itself.
///
/// <para>Metriox can reconstruct a label server-side by matching the payload against the keyboards it has
/// recorded for a message id. That works, but it cannot be exact on the Bot-API path: a message keeps its id
/// across an edit, so a bot that navigates by editing one message in place gives that id a sequence of
/// keyboards — and the ingest endpoint stamps one clock reading per HTTP request, so a press and the edit it
/// triggered (milliseconds apart, usually in the same POST) cannot be ordered against each other.</para>
///
/// <para>Telegram removes the guesswork: <c>callback_query.message.reply_markup</c> is the keyboard exactly
/// as the user saw it. These pin that we actually read it.</para>
/// </summary>
public class CallbackButtonLabelTests
{
    /// <summary>
    /// Root menu and the submenu it becomes. <c>menu_connect</c> appears in BOTH under DIFFERENT labels,
    /// which is the case that makes server-side reconstruction risky rather than merely incomplete: get the
    /// version wrong and the transcript names a button the user never pressed.
    /// </summary>
    private static InlineKeyboardMarkup Menu => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📇 Профиль", "menu_profile"),
            InlineKeyboardButton.WithCallbackData("💳 Оформить подписку", "menu_connect"),
        },
    });

    private static InlineKeyboardMarkup Profile => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("💳 Продлить подписку", "menu_connect"),
            InlineKeyboardButton.WithUrl("🌐 Подключиться", "https://example.invalid/sub"),
        },
        new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "menu_root") },
    });

    [Fact]
    public void FindLabel_returns_the_label_for_a_payload()
    {
        Assert.Equal("📇 Профиль", InlineKeyboardSerializer.FindLabel(Menu, "menu_profile"));
        Assert.Equal("◀️ Назад", InlineKeyboardSerializer.FindLabel(Profile, "menu_root"));
    }

    [Fact]
    public void FindLabel_answers_per_keyboard_when_one_payload_carries_two_labels()
    {
        // Same payload, same message id in production, two different correct answers.
        Assert.Equal("💳 Оформить подписку", InlineKeyboardSerializer.FindLabel(Menu, "menu_connect"));
        Assert.Equal("💳 Продлить подписку", InlineKeyboardSerializer.FindLabel(Profile, "menu_connect"));
    }

    [Fact]
    public void FindLabel_searches_every_row_not_just_the_first()
    {
        Assert.Equal("◀️ Назад", InlineKeyboardSerializer.FindLabel(Profile, "menu_root"));
    }

    [Fact]
    public void FindLabel_degrades_to_null_rather_than_guessing()
    {
        Assert.Null(InlineKeyboardSerializer.FindLabel(Menu, "never_existed"));
        Assert.Null(InlineKeyboardSerializer.FindLabel(Menu, null));
        Assert.Null(InlineKeyboardSerializer.FindLabel(Menu, ""));
        Assert.Null(InlineKeyboardSerializer.FindLabel(null, "menu_profile"));

        // A url button generates no callback_query, so it can never be the pressed button.
        Assert.Null(InlineKeyboardSerializer.FindLabel(Profile, "https://example.invalid/sub"));
    }

    [Fact]
    public void FindLabel_is_case_sensitive_like_the_payload_comparison_upstream()
    {
        Assert.Null(InlineKeyboardSerializer.FindLabel(Menu, "Menu_Profile"));
    }

    [Fact]
    public void A_press_reports_the_label_it_was_handed()
    {
        var e = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(Press("menu_connect", Profile));

        Assert.NotNull(e);
        Assert.Equal("💳 Продлить подписку", e!.PropsString?["tg.callback_button_text"]);
        Assert.Equal("menu_connect", e.PropsString?["tg.callback_data"]);
    }

    [Fact]
    public void A_press_with_no_keyboard_attached_omits_the_label_entirely()
    {
        // Both callback_query.message and message.reply_markup are optional — absent for inline-mode
        // buttons and for messages Telegram no longer treats as accessible. The prop must then be MISSING
        // rather than empty, so Metriox falls back to its own reconstruction instead of believing a blank.
        var e = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(Press("menu_connect", markup: null));

        Assert.NotNull(e);
        Assert.False(e!.PropsString?.ContainsKey("tg.callback_button_text") ?? false);
    }

    private static Update Press(string data, InlineKeyboardMarkup? markup) => new()
    {
        Id = 1,
        CallbackQuery = new CallbackQuery
        {
            Id = "cbq-1",
            Data = data,
            From = new User { Id = 160266, IsBot = false, FirstName = "Tester" },
            Message = new Message
            {
                Id = 5000,
                Date = new DateTime(2026, 9, 9, 20, 50, 16, DateTimeKind.Utc),
                Chat = new Chat { Id = 160266, Type = ChatType.Private },
                ReplyMarkup = markup,
            },
        },
    };
}
