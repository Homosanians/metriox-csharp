using Metriox.SDK.Transport.Contracts;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Pulls the acting person out of an update, so their identity travels with the event.
///
/// <para>Every Bot-API update that a person causes carries a full <c>User</c> object — id, first name,
/// @username where they have one, language, Premium flag. The event mapper keeps only the id, because
/// that is all an event needs; this reads the rest, once, so <see cref="BotEventsRequest.Users"/> can
/// carry it.</para>
///
/// <para><b>Deliberately a separate switch from the event mapper's.</b> That one is long, load-bearing
/// and full of per-type analytics; threading a second return value through all twenty-odd cases would
/// risk the capture path to save a file. This one asks a single question of each update and can be
/// read, tested and extended on its own.</para>
///
/// <para>Bots are never returned. The bot's own sends are captured separately, and a bot in the user
/// list would inflate every user count with the account doing the counting.</para>
/// </summary>
public static class TelegramUserSnapshotExtractor
{
    /// <summary>
    /// The human behind <paramref name="u"/>, or <see langword="null"/> when the update names none —
    /// a channel post signed by the channel, a reaction from an anonymous admin, a poll closing on a
    /// timer, a deleted-messages notice.
    /// </summary>
    public static BotUserSnapshot? From(Update? u)
    {
        if (u is null) return null;

        var from =
            u.Message?.From
            ?? u.EditedMessage?.From
            ?? u.ChannelPost?.From
            ?? u.EditedChannelPost?.From
            ?? u.BusinessMessage?.From
            ?? u.EditedBusinessMessage?.From
            ?? u.CallbackQuery?.From
            ?? u.InlineQuery?.From
            ?? u.ChosenInlineResult?.From
            ?? u.ShippingQuery?.From
            ?? u.PreCheckoutQuery?.From
            ?? u.PurchasedPaidMedia?.From
            ?? u.MyChatMember?.From
            ?? u.ChatMember?.From
            ?? u.ChatJoinRequest?.From
            ?? u.BusinessConnection?.User
            // Both of these are nullable on purpose in the Bot API: a reaction can come from a chat
            // acting anonymously, and a poll can be answered on behalf of a chat.
            ?? u.MessageReaction?.User
            ?? u.PollAnswer?.User;

        return Of(from);
    }

    /// <summary>
    /// A snapshot of one Telegram user, or <see langword="null"/> for a bot or a missing user. Public
    /// so a caller that already holds a <c>User</c> — from its own update loop, or from a Bot-API call
    /// it made itself — can report identity without going through an <see cref="Update"/>.
    /// </summary>
    public static BotUserSnapshot? Of(User? user)
    {
        if (user is null || user.IsBot) return null;

        return new BotUserSnapshot
        {
            TelegramUserId = user.Id,
            Username = NullIfEmpty(user.Username),
            FirstName = NullIfEmpty(user.FirstName),
            LastName = NullIfEmpty(user.LastName),
            LanguageCode = NullIfEmpty(user.LanguageCode),
            // Telegram reports this only when true; false and "not reported" are the same observation
            // from a bot's point of view, so both are sent as false rather than one of them as null.
            IsPremium = user.IsPremium
        };
    }

    /// <summary>
    /// The person on the other side of a private chat, or <see langword="null"/> for any other chat
    /// type.
    ///
    /// <para>In a private chat the chat id IS the user id and the chat carries their name and
    /// @username, which makes an outgoing message an identity source: a bot that mostly answers, and
    /// whose users say little, would otherwise be a dashboard full of numbers. Everything else — a
    /// group, a supergroup, a channel — is a place, not a person, and returns null: recording a group
    /// as a user is how a "user" with a negative id gets invented.</para>
    /// </summary>
    public static BotUserSnapshot? OfPrivateChat(Chat? chat)
    {
        if (chat is null || chat.Type != ChatType.Private) return null;

        return new BotUserSnapshot
        {
            TelegramUserId = chat.Id,
            Username = NullIfEmpty(chat.Username),
            FirstName = NullIfEmpty(chat.FirstName),
            LastName = NullIfEmpty(chat.LastName),
            // A Chat carries neither, and guessing false for a Premium user would be worse than
            // leaving the profile's existing value alone.
            LanguageCode = null,
            IsPremium = null
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
