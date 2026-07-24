using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot.Types;

namespace Metriox.SDK.Telegram;

/// <summary>
/// Cross-producer event identity. This is a MIRROR of the backend's <c>TelegramEventKeys</c> +
/// <c>DeterministicUuid</c>; the key strings and the hash are a published contract, documented in
/// metriox-docs. Both sides must agree byte-for-byte or nothing dedups — change neither casually.
///
/// <para><b>Why not update_id.</b> The SDK used to key event ids on
/// <c>tg:update:{bot}:{update_id}</c>. That can never match Metriox's MTProto worker, because MTProto
/// has no <c>update_id</c> at all: TL sequences updates with per-session <c>pts</c>/<c>qts</c> cursors,
/// which are a property of the client session rather than of the event. So a message captured by both
/// the bot's SDK and the worker produced two unrelated ids and therefore two rows. These keys are built
/// from the coordinates BOTH sides observe.</para>
///
/// <para><b>Why the bot id is absent.</b> Uniqueness only has to hold within one bot, which the server
/// already scopes by. Including it here would be worse than redundant: the server keys on its own
/// internal bot id, which an SDK cannot know, so any key containing a bot identifier is unmatchable
/// by construction.</para>
/// </summary>
public static class TelegramEventIdentity
{
    // Must equal the backend's DeterministicUuid namespace. Every byte of this GUID is repeated within
    // its field, so .NET's mixed-endian ToByteArray() layout and the RFC byte order coincide -- which is
    // the only reason a non-.NET SDK can reproduce this hash without replicating .NET quirks.
    private static readonly Guid Namespace = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>UUID v5 (SHA-1) over the shared namespace, matching the backend exactly.</summary>
    public static Guid Uuid5(string value)
    {
        var namespaceBytes = Namespace.ToByteArray();
        var valueBytes = Encoding.UTF8.GetBytes(value);

        var data = new byte[namespaceBytes.Length + valueBytes.Length];
        namespaceBytes.CopyTo(data, 0);
        valueBytes.CopyTo(data, namespaceBytes.Length);

        var hash = SHA1.HashData(data);

        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // variant

        return new Guid(guidBytes);
    }

    public static string Message(long chatId, long messageId) => $"tg:msg:{chatId}:{messageId}";

    public static string MessageEdit(long chatId, long messageId, long editUnixSeconds)
        => $"tg:edit:{chatId}:{messageId}:{editUnixSeconds}";

    public static string BusinessMessage(long chatId, long messageId) => $"tg:bizmsg:{chatId}:{messageId}";

    public static string CallbackQuery(string queryId) => $"tg:cbq:{queryId}";

    public static string InlineQuery(string queryId) => $"tg:iq:{queryId}";

    public static string ChosenInlineResult(long userId, string resultId) => $"tg:isend:{userId}:{resultId}";

    public static string PreCheckoutQuery(string queryId) => $"tg:pcq:{queryId}";

    public static string ShippingQuery(string queryId) => $"tg:sq:{queryId}";

    public static string Reactions(long chatId, long messageId) => $"tg:react:{chatId}:{messageId}";

    public static string PollVote(long chatId, string pollId) => $"tg:pollvote:{chatId}:{pollId}";

    /// <summary>
    /// The natural key for an inbound update, or <c>null</c> when the update carries no coordinate both
    /// producers can see (membership changes, boosts, business-connection notices …). Callers fall back
    /// to an update-id key for those: it is still idempotent for THIS producer, which is all it can be.
    /// </summary>
    public static string? ForUpdate(Update u)
    {
        // A message-bearing update is keyed on chat + message id, and an edit additionally on edit time,
        // so each edit is its own event rather than collapsing onto the original send.
        var message = u.Message ?? u.EditedMessage ?? u.ChannelPost ?? u.EditedChannelPost;
        if (message is not null)
            return message.EditDate is { } edited
                ? MessageEdit(message.Chat.Id, message.Id, ToUnixSeconds(edited))
                : Message(message.Chat.Id, message.Id);

        var business = u.BusinessMessage ?? u.EditedBusinessMessage;
        if (business is not null)
            return BusinessMessage(business.Chat.Id, business.Id);

        if (u.CallbackQuery is { } cbq) return CallbackQuery(cbq.Id);
        if (u.InlineQuery is { } iq) return InlineQuery(iq.Id);
        if (u.ChosenInlineResult is { } cir) return ChosenInlineResult(cir.From.Id, cir.ResultId);
        if (u.PreCheckoutQuery is { } pcq) return PreCheckoutQuery(pcq.Id);
        if (u.ShippingQuery is { } sq) return ShippingQuery(sq.Id);
        if (u.MessageReaction is { } mr) return Reactions(mr.Chat.Id, mr.MessageId);

        if (u.PollAnswer is { } pa)
        {
            // A vote is keyed by the voter's chat: an anonymous vote in a channel poll reports VoterChat
            // instead of User.
            var voterChatId = pa.VoterChat?.Id ?? pa.User?.Id;
            if (voterChatId is { } id) return PollVote(id, pa.PollId);
        }

        return null;
    }

    /// <summary>Telegram's own timestamp unit — never .NET ticks, which no other runtime can reproduce.</summary>
    public static long ToUnixSeconds(DateTime utc)
        => utc == default
            ? 0
            : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
}
