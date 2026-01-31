using Metriox.SDK.Transport.Contracts;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Telegram Update -> BotEvent mapper with configurable exclude list.
/// Ensures every UpdateType enum value is explicitly handled.
/// </summary>
public sealed class TelegramUpdateToBotEventMapper
{
    private readonly string _platformBotId;
    private readonly HashSet<UpdateType> _excluded;

    /// <param name="platformBotId">Your Telegram bot identifier (username or numeric id as string).</param>
    /// <param name="excludedTypes">Optional list of UpdateTypes to ignore (return null).</param>
    public TelegramUpdateToBotEventMapper(string platformBotId, IEnumerable<UpdateType>? excludedTypes = null)
    {
        _platformBotId = platformBotId ?? throw new ArgumentNullException(nameof(platformBotId));
        _excluded = excludedTypes is null ? new HashSet<UpdateType>() : new HashSet<UpdateType>(excludedTypes);
    }

    /// <summary>
    /// Maps Telegram Update to BotEvent. Returns null if the update type is excluded.
    /// </summary>
    public BotEvent? ToBotEvent(Update u, string? platformBotIdOverride = null, DateTimeOffset? receivedAtUtc = null)
    {
        if (u is null) throw new ArgumentNullException(nameof(u));

        var platformBotId = _platformBotId;

        if (!string.IsNullOrEmpty(platformBotIdOverride))
        {
            platformBotId = platformBotIdOverride;
        }
        
        // Exclude early by enum (cheap)
        var updateType = u.Type;

        if (_excluded.Contains(updateType))
            return null;

        var now = receivedAtUtc ?? DateTimeOffset.UtcNow;

        // Deterministic eventId for idempotency: stable for (bot, update_id)
        var eventId = DeterministicGuid($"tg:update:{platformBotId}:{u.Id}");

        const string source = "tg";
        const string eventOrigin = "platform";

        string eventType;
        string eventName;
        DateTimeOffset eventDate = now;
        string? text = null;

        var propsString = new Dictionary<string, string>(capacity: 8);
        var propsLong = new Dictionary<string, long>(capacity: 8);
        Dictionary<string, bool>? propsBool = null;

        propsLong["tg.update_id"] = u.Id;
        propsString["tg.update_type"] = updateType.ToString();

        string? platformUserId = null;

        switch (updateType)
        {
            case UpdateType.Unknown:
            {
                eventType = "platform";
                eventName = "unknown";
                eventDate = now;

                break;
            }

            case UpdateType.Message:
            {
                var m = u.Message!;
                eventType = "message";
                eventName = m.Text is not null ? "text_message" : "message";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);
                
                AddMessageAnalytics(m, updateType, propsString, propsLong, ref propsBool);

                if (m.From?.Id != null)
                    platformUserId = m.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                if (!string.IsNullOrEmpty(m.Chat.Username))
                    propsString["tg.chat_username"] = m.Chat.Username!;

                if (!string.IsNullOrEmpty(m.From?.Username))
                    propsString["tg.from_username"] = m.From.Username!;

                break;
            }

            case UpdateType.EditedMessage:
            {
                var m = u.EditedMessage!;
                eventType = "message";
                eventName = "edited_message";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);

                if (m.From?.Id != null)
                    platformUserId = m.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                break;
            }

            case UpdateType.ChannelPost:
            {
                var m = u.ChannelPost!;
                eventType = "message";
                eventName = "channel_post";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                if (!string.IsNullOrEmpty(m.Chat.Username))
                    propsString["tg.chat_username"] = m.Chat.Username!;

                break;
            }

            case UpdateType.EditedChannelPost:
            {
                var m = u.EditedChannelPost!;
                eventType = "message";
                eventName = "edited_channel_post";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                if (!string.IsNullOrEmpty(m.Chat.Username))
                    propsString["tg.chat_username"] = m.Chat.Username!;

                break;
            }

            case UpdateType.CallbackQuery:
            {
                var cq = u.CallbackQuery!;
                eventType = "interaction";
                eventName = "callback_query";

                eventDate = now; // no reliable event timestamp
                platformUserId = cq.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.callback_data"] = cq.Data ?? string.Empty;

                if (!string.IsNullOrEmpty(cq.Id))
                    propsString["tg.callback_id"] = cq.Id;

                if (cq.Message is not null)
                {
                    propsLong["tg.message_id"] = cq.Message.MessageId;
                    propsLong["tg.chat_id"] = cq.Message.Chat.Id;
                    propsString["tg.chat_type"] = cq.Message.Chat.Type.ToString();

                    // optional text of message being interacted with
                    text = cq.Message.Text;
                }
                else
                {
                    // CallbackQuery might be from an inline message
                    if (!string.IsNullOrEmpty(cq.InlineMessageId))
                        propsString["tg.inline_message_id"] = cq.InlineMessageId!;
                }

                break;
            }

            case UpdateType.InlineQuery:
            {
                var iq = u.InlineQuery!;
                eventType = "interaction";
                eventName = "inline_query";

                eventDate = now;
                platformUserId = iq.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.query"] = iq.Query ?? string.Empty;
                propsString["tg.inline_query_id"] = iq.Id;

                break;
            }

            case UpdateType.ChosenInlineResult:
            {
                var r = u.ChosenInlineResult!;
                eventType = "interaction";
                eventName = "chosen_inline_result";

                eventDate = now;
                platformUserId = r.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.chosen_inline_result_id"] = r.ResultId;

                if (!string.IsNullOrEmpty(r.Query))
                    propsString["tg.query"] = r.Query!;

                break;
            }

            case UpdateType.ShippingQuery:
            {
                var sq = u.ShippingQuery!;
                eventType = "payment";
                eventName = "shipping_query";

                eventDate = now;
                platformUserId = sq.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.shipping_query_id"] = sq.Id;
                propsString["tg.invoice_payload"] = sq.InvoicePayload;

                break;
            }

            case UpdateType.PreCheckoutQuery:
            {
                var pq = u.PreCheckoutQuery!;
                eventType = "payment";
                eventName = "pre_checkout_query";

                eventDate = now;
                platformUserId = pq.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.pre_checkout_query_id"] = pq.Id;
                propsString["tg.currency"] = pq.Currency;
                propsLong["tg.total_amount"] = pq.TotalAmount;
                propsString["tg.invoice_payload"] = pq.InvoicePayload;

                break;
            }

            case UpdateType.PurchasedPaidMedia:
            {
                var ppm = u.PurchasedPaidMedia!;
                eventType = "payment";
                eventName = "purchased_paid_media";

                eventDate = now;
                platformUserId = ppm.From.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.paid_media_payload"] = ppm.PaidMediaPayload;

                break;
            }

            case UpdateType.Poll:
            {
                var p = u.Poll!;
                eventType = "interaction";
                eventName = "poll";

                eventDate = now;
                propsString["tg.poll_id"] = p.Id;
                propsString["tg.poll_question"] = p.Question;

                propsBool = new Dictionary<string, bool>(capacity: 2)
                {
                    ["tg.poll_is_anonymous"] = p.IsAnonymous
                };

                propsString["tg.poll_type"] = p.Type.ToString();

                break;
            }

            case UpdateType.PollAnswer:
            {
                var pa = u.PollAnswer!;
                eventType = "interaction";
                eventName = "poll_answer";

                eventDate = now;
                platformUserId = pa.User.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.poll_id"] = pa.PollId;
                propsLong["tg.option_count"] = pa.OptionIds?.Length ?? 0;

                break;
            }

            case UpdateType.MyChatMember:
            {
                var cm = u.MyChatMember!;
                eventType = "membership";
                eventName = "my_chat_member";

                eventDate = cm.Date != default ? cm.Date : now;
                platformUserId = cm.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = cm.Chat.Id;
                propsString["tg.chat_type"] = cm.Chat.Type.ToString();
                propsString["tg.old_status"] = cm.OldChatMember.Status.ToString();
                propsString["tg.new_status"] = cm.NewChatMember.Status.ToString();

                break;
            }

            case UpdateType.ChatMember:
            {
                var cm = u.ChatMember!;
                eventType = "membership";
                eventName = "chat_member";

                eventDate = cm.Date != default ? cm.Date : now;
                platformUserId = cm.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = cm.Chat.Id;
                propsString["tg.chat_type"] = cm.Chat.Type.ToString();
                propsString["tg.old_status"] = cm.OldChatMember.Status.ToString();
                propsString["tg.new_status"] = cm.NewChatMember.Status.ToString();

                break;
            }

            case UpdateType.ChatJoinRequest:
            {
                var jr = u.ChatJoinRequest!;
                eventType = "membership";
                eventName = "chat_join_request";

                eventDate = jr.Date != default ? jr.Date : now;
                platformUserId = jr.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = jr.Chat.Id;
                propsString["tg.chat_type"] = jr.Chat.Type.ToString();

                if (!string.IsNullOrEmpty(jr.UserChatId.ToString()))
                    propsLong["tg.user_chat_id"] = jr.UserChatId;

                break;
            }

            case UpdateType.MessageReaction:
            {
                var mr = u.MessageReaction!;
                eventType = "interaction";
                eventName = "message_reaction";

                eventDate = mr.Date != default ? mr.Date : now;
                platformUserId = mr.User?.Id.ToString(CultureInfo.InvariantCulture); // might be null for anonymous?

                propsLong["tg.chat_id"] = mr.Chat.Id;
                propsLong["tg.message_id"] = mr.MessageId;

                // Keep it lightweight: store counts as strings if needed; reaction objects vary
                propsString["tg.reaction_change"] = "updated";

                break;
            }

            case UpdateType.MessageReactionCount:
            {
                var mrc = u.MessageReactionCount!;
                eventType = "interaction";
                eventName = "message_reaction_count";

                eventDate = mrc.Date != default ? mrc.Date : now;

                propsLong["tg.chat_id"] = mrc.Chat.Id;
                propsLong["tg.message_id"] = mrc.MessageId;
                propsLong["tg.reaction_count_items"] = mrc.Reactions?.Length ?? 0;

                break;
            }

            case UpdateType.ChatBoost:
            {
                var cb = u.ChatBoost!;
                eventType = "interaction";
                eventName = "chat_boost";

                eventDate = now;

                propsLong["tg.chat_id"] = cb.Chat.Id;

                if (!string.IsNullOrEmpty(cb.Boost.BoostId))
                    propsString["tg.boost_id"] = cb.Boost.BoostId;

                propsString["tg.boost_add_date"] = cb.Boost.AddDate.ToString("O");
                propsString["tg.boost_expiration_date"] = cb.Boost.ExpirationDate.ToString("O");

                break;
            }

            case UpdateType.RemovedChatBoost:
            {
                var rcb = u.RemovedChatBoost!;
                eventType = "interaction";
                eventName = "removed_chat_boost";

                eventDate = now;
                propsLong["tg.chat_id"] = rcb.Chat.Id;
                propsString["tg.boost_id"] = rcb.BoostId;

                break;
            }

            case UpdateType.BusinessConnection:
            {
                var bc = u.BusinessConnection!;
                eventType = "business";
                eventName = "business_connection";
                eventDate = bc.Date != default ? bc.Date : now;

                platformUserId = bc.User?.Id.ToString(CultureInfo.InvariantCulture);

                propsString["tg.business_connection_id"] = bc.Id;

                propsBool = new Dictionary<string, bool>(capacity: 1)
                {
                    ["tg.business_is_enabled"] = bc.IsEnabled
                };

                if (bc.UserChatId != 0)
                    propsLong["tg.user_chat_id"] = bc.UserChatId;

                if (bc.Rights is not null)
                    propsString["tg.business_rights"] = bc.Rights.ToString()!;

                break;
            }

            case UpdateType.BusinessMessage:
            {
                var m = u.BusinessMessage!;
                eventType = "message";
                eventName = "business_message";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);

                if (m.From?.Id != null)
                    platformUserId = m.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                break;
            }

            case UpdateType.EditedBusinessMessage:
            {
                var m = u.EditedBusinessMessage!;
                eventType = "message";
                eventName = "edited_business_message";

                if (m.Date != default)
                    eventDate = m.Date;

                text = m.Text;

                _ = TryAddCommandProps(text, propsString);

                if (m.From?.Id != null)
                    platformUserId = m.From.Id.ToString(CultureInfo.InvariantCulture);

                propsLong["tg.chat_id"] = m.Chat.Id;
                propsString["tg.chat_type"] = m.Chat.Type.ToString();

                break;
            }

            case UpdateType.DeletedBusinessMessages:
            {
                var del = u.DeletedBusinessMessages!;
                eventType = "message";
                eventName = "deleted_business_messages";

                eventDate = now;

                propsString["tg.business_connection_id"] = del.BusinessConnectionId;
                propsLong["tg.chat_id"] = del.Chat.Id;
                propsLong["tg.deleted_message_ids_count"] = del.MessageIds?.Length ?? 0;

                break;
            }

            default:
            {
                // If Telegram adds a new UpdateType and your package updates,
                // this default will still work and you'll still have tg.update_type recorded.
                eventType = "platform";
                eventName = updateType.ToString();
                eventDate = now;

                break;
            }
        }

        // Optional: exclude after reading (if you want to exclude by derived name)
        // if (_excluded.Contains(updateType)) return null; // already done

        // Null-out empty props to avoid sending {} in JSON
        var ps = propsString.Count > 0 ? propsString : null;
        var pl = propsLong.Count > 0 ? propsLong : null;
        var pb = propsBool is { Count: > 0 } ? propsBool : null;

        return new BotEvent
        {
            EventId = eventId,
            Source = source,
            PlatformBotId = platformBotId,
            PlatformUserId = platformUserId,
            EventOrigin = eventOrigin,
            EventType = eventType,
            EventName = eventName,
            EventDate = eventDate,
            Text = text,
            PropsString = ps,
            PropsLong = pl,
            PropsBool = pb
        };
    }

    private bool TryAddCommandProps(string? text, Dictionary<string, string> propsString)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().TrimStart();

        if (span.Length < 2 || span[0] != '/')
            return false;

        // Read first token (up to whitespace)
        int i = 1;

        while (i < span.Length && !char.IsWhiteSpace(span[i]))
            i++;

        var token = span[..i]; // "/start@MyBot" or "/start"
        propsString["command_token"] = token.ToString();

        // Split out @mention if present
        var at = token.IndexOf('@');
        ReadOnlySpan<char> commandSpan = at >= 0 ? token[..at] : token;
        ReadOnlySpan<char> mentionSpan = at >= 0 ? token[(at + 1)..] : default;

        if (commandSpan.Length < 2)
            return false;

        propsString["command"] = commandSpan.ToString();

        if (!mentionSpan.IsEmpty)
            propsString["command_mention"] = mentionSpan.ToString(); // no '@'

        // Params: everything after token
        var paramsSpan = span[i..].Trim();

        if (!paramsSpan.IsEmpty)
            propsString["command_params"] = paramsSpan.ToString();

        return true;
    }

    private static Dictionary<string, bool> EnsureBoolProps(ref Dictionary<string, bool>? propsBool)
        => propsBool ??= new Dictionary<string, bool>(capacity: 8);

    private static void AddMessageAnalytics(
        Message m,
        UpdateType updateType,
        Dictionary<string, string> propsString,
        Dictionary<string, long> propsLong,
        ref Dictionary<string, bool>? propsBool)
    {
        // --- Message identity & type ---
        propsLong["tg.message_id"] = m.Id;
        propsString["tg.message_type"] = m.Type.ToString();

        var pb = EnsureBoolProps(ref propsBool);

        // Edited flags: treat Edited* updates as edited even if EditDate is missing
        var editedByUpdateType =
            updateType is UpdateType.EditedMessage
                or UpdateType.EditedChannelPost
                or UpdateType.EditedBusinessMessage;

        pb["tg.is_edited"] = editedByUpdateType || m.EditDate is not null;

        if (m.EditDate != null && m.EditDate is DateTime editDate)
            propsString["tg.edit_date"] = editDate.ToString("O");

        // --- Text/caption metrics ---
        if (m.Text is { Length: > 0 } t)
            propsLong["tg.text_len"] = t.Length;

        if (m.Caption is { Length: > 0 } c)
            propsLong["tg.caption_len"] = c.Length;

        // --- Actor safety coverage ---
        if (m.From is not null)
        {
            propsLong["tg.from_id"] = m.From.Id;
            pb["tg.from_is_bot"] = m.From.IsBot;
        }

        pb["tg.has_sender_chat"] = m.SenderChat is not null;

        if (m.SenderChat is not null)
        {
            propsLong["tg.sender_chat_id"] = m.SenderChat.Id;
            propsString["tg.sender_chat_type"] = m.SenderChat.Type.ToString();
        }

        // --- Reply/threading ---
        pb["tg.is_reply"] = m.ReplyToMessage is not null;

        if (m.ReplyToMessage is not null)
            propsLong["tg.reply_to_message_id"] = m.ReplyToMessage.Id;

        if (m.MessageThreadId is int tid)
            propsLong["tg.message_thread_id"] = tid;

        // --- Entities summary ---
        var entities = m.Entities;
        var captionEntities = m.CaptionEntities;

        propsLong["tg.entities_count"] = entities?.Length ?? 0;
        propsLong["tg.caption_entities_count"] = captionEntities?.Length ?? 0;

        pb["tg.has_bot_command_entity"] = HasEntityType(entities, MessageEntityType.BotCommand)
                                          || HasEntityType(captionEntities, MessageEntityType.BotCommand);

        pb["tg.has_url_entity"] = HasEntityType(entities, MessageEntityType.Url)
                                  || HasEntityType(captionEntities, MessageEntityType.Url);

        // Mentions can be "Mention" (@username) or "TextMention" (user without username)
        pb["tg.has_mention_entity"] = HasEntityType(entities, MessageEntityType.Mention)
                                      || HasEntityType(captionEntities, MessageEntityType.Mention)
                                      || HasEntityType(entities, MessageEntityType.TextMention)
                                      || HasEntityType(captionEntities, MessageEntityType.TextMention);
    }

    private static bool HasEntityType(MessageEntity[]? entities, MessageEntityType type)
    {
        if (entities is null || entities.Length == 0) return false;

        foreach (var e in entities)
        {
            if (e.Type == type) return true;
        }

        return false;
    }

    /// <summary>
    /// Stable Guid from string for idempotency.
    /// </summary>
    private static Guid DeterministicGuid(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));

        return new Guid(bytes);
    }
}