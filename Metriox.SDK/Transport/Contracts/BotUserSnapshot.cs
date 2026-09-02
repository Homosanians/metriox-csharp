using System.Text.Json.Serialization;

namespace Metriox.SDK.Transport.Contracts;

/// <summary>
/// Who a person is, as Telegram described them on the update that carried them.
///
/// <para><b>Why this exists as its own thing.</b> An event says what happened and who did it (an id);
/// it does not say who that person <i>is</i>. Telegram's <c>from</c> object carries the name, the
/// @username, the language and the Premium flag, and none of those belong on an event — they describe
/// the person, not the moment, and would be duplicated onto every row they appear in. Metriox keeps
/// them in a separate profile table, and this is what fills it.</para>
///
/// <para>Without it, a bot's users appear in Metriox as bare numeric ids: the MTProto capture path
/// cannot see private chats at all (Telegram delivers a bot's DMs only through the Bot API), so for a
/// direct-message bot the SDK is the ONLY thing that can report who its users are.</para>
/// </summary>
public sealed class BotUserSnapshot
{
    /// <summary>The Telegram user id. The only required field — everything else is optional on Telegram.</summary>
    [JsonPropertyName("telegramUserId")]
    public required long TelegramUserId { get; init; }

    /// <summary>@username, without the @. Most Telegram accounts do not have one.</summary>
    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; init; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; init; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; init; }

    /// <summary>IETF tag of the user's Telegram client language, e.g. <c>ru</c> or <c>en</c>.</summary>
    [JsonPropertyName("languageCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LanguageCode { get; init; }

    [JsonPropertyName("isPremium")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPremium { get; init; }
}
