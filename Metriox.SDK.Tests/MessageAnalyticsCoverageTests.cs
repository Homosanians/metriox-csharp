using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Metriox.SDK.Tests;

/// <summary>
/// Every message-bearing update must carry the same message props. Five branches of the update mapper
/// skipped <c>AddMessageAnalytics</c>, so an edited message, a channel post or a business message arrived
/// with only <c>chat_id</c>/<c>chat_type</c> — no <c>tg.message_id</c>, and after the direction/entities
/// work, no <c>tg.direction</c> and no <c>tg.entities</c> either. Measured in production: an edited
/// message landed with 10 props where a new message had 17.
///
/// <para>This was an omission rather than a decision — <c>AddMessageAnalytics</c> already special-cases
/// the <c>Edited*</c> update types internally (<c>editedByUpdateType</c>), so it was written expecting
/// these call sites to exist.</para>
/// </summary>
public class MessageAnalyticsCoverageTests
{
    private const long ChatId = 312302365;
    private const long UserId = 999001;

    private static Message Msg(int id = 9577, ChatType chatType = ChatType.Private, DateTime? editDate = null) => new()
    {
        Id = id,
        Chat = new Chat { Id = ChatId, Type = chatType, Username = "somechat" },
        From = new User { Id = UserId, Username = "someone", FirstName = "Some" },
        Date = new DateTime(2026, 7, 25, 11, 59, 28, DateTimeKind.Utc),
        EditDate = editDate,
        Text = "hello world"
    };

    private static readonly DateTime Edited = new(2026, 7, 25, 12, 18, 27, DateTimeKind.Utc);

    public static TheoryData<string, Update> MessageBearingUpdates() => new()
    {
        { "Message",              new Update { Id = 1, Message = Msg() } },
        { "EditedMessage",        new Update { Id = 2, EditedMessage = Msg(editDate: Edited) } },
        { "ChannelPost",          new Update { Id = 3, ChannelPost = Msg(chatType: ChatType.Channel) } },
        { "EditedChannelPost",    new Update { Id = 4, EditedChannelPost = Msg(chatType: ChatType.Channel, editDate: Edited) } },
        { "BusinessMessage",      new Update { Id = 5, BusinessMessage = Msg() } },
        { "EditedBusinessMessage", new Update { Id = 6, EditedBusinessMessage = Msg(editDate: Edited) } },
    };

    [Theory]
    [MemberData(nameof(MessageBearingUpdates))]
    public void EveryMessageBearingUpdate_CarriesMessageId(string label, Update update)
    {
        var ev = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(update);

        Assert.NotNull(ev);
        Assert.True(ev!.PropsLong?.ContainsKey("tg.message_id"),
            $"{label} must carry tg.message_id — without it the event cannot be matched to the same event " +
            "captured by the MTProto worker, and the UI cannot link it to a conversation");
        Assert.Equal(9577, ev.PropsLong!["tg.message_id"]);
    }

    [Theory]
    [MemberData(nameof(MessageBearingUpdates))]
    public void EveryMessageBearingUpdate_CarriesDirection(string label, Update update)
    {
        // The Bot API only ever delivers messages the bot did not send, so anything it reports is inbound.
        // Without this the transcript falls back to a rendering default and every bubble looks the same.
        var ev = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(update);

        Assert.Equal("inbound", ev!.PropsString?["tg.direction"]);
    }

    [Theory]
    [MemberData(nameof(MessageBearingUpdates))]
    public void EveryMessageBearingUpdate_CarriesMessageTypeAndTextLength(string label, Update update)
    {
        var ev = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(update);

        Assert.True(ev!.PropsString?.ContainsKey("tg.message_type"), $"{label}: tg.message_type");
        Assert.Equal("hello world".Length, ev.PropsLong?["tg.text_len"]);
    }

    [Theory]
    [MemberData(nameof(MessageBearingUpdates))]
    public void EveryMessageBearingUpdate_CarriesChatIdentity(string label, Update update)
    {
        var ev = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(update);

        Assert.Equal(ChatId, ev!.PropsLong?["tg.chat_id"]);
        Assert.True(ev.PropsString?.ContainsKey("tg.chat_type"), $"{label}: tg.chat_type");
        Assert.Equal("somechat", ev.PropsString?["tg.chat_username"]);
    }

    [Fact]
    public void EditedMessage_IsFlaggedAsEdited_AndCarriesItsEditDate()
    {
        var ev = new TelegramUpdateToBotEventMapper("mybot")
            .ToBotEvent(new Update { Id = 1, EditedMessage = Msg(editDate: Edited) });

        Assert.True(ev!.PropsBool?["tg.is_edited"]);
        Assert.True(ev.PropsString?.ContainsKey("tg.edit_date"));
    }

    [Fact]
    public void EditedMessage_GetsItsOwnEventId_DistinctFromTheOriginalSend()
    {
        // The edit and the send are separate events for the same message id, so the edit timestamp has to
        // be part of the identity or the edit would collapse onto the send.
        var mapper = new TelegramUpdateToBotEventMapper("mybot");

        var original = mapper.ToBotEvent(new Update { Id = 1, Message = Msg() })!;
        var edited = mapper.ToBotEvent(new Update { Id = 2, EditedMessage = Msg(editDate: Edited) })!;

        Assert.NotEqual(original.EventId, edited.EventId);
    }

    [Fact]
    public void EditedMessage_KeepsTheSenderIdentity()
    {
        // Was dropped along with the rest: an edit reported no from_username, so the same person looked
        // like a different, less-identified user depending on whether they edited their message.
        var ev = new TelegramUpdateToBotEventMapper("mybot")
            .ToBotEvent(new Update { Id = 1, EditedMessage = Msg(editDate: Edited) });

        Assert.Equal(UserId.ToString(), ev!.PlatformUserId);
        Assert.Equal("someone", ev.PropsString?["tg.from_username"]);
    }
}
