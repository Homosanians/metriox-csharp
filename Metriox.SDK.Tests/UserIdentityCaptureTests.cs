using Metriox.SDK;
using Metriox.SDK.Telegram;
using Metriox.SDK.Telegram.Mappers;
using Metriox.SDK.Transport;
using Metriox.SDK.Transport.Contracts;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Metriox.SDK.Tests;

/// <summary>
/// Who the bot's users are, as opposed to what they did.
///
/// <para>Metriox has always accepted a <c>users[]</c> array and this SDK never filled it, so every
/// person a Bot-API bot serves arrived as a bare numeric id. It matters more here than anywhere else:
/// Telegram delivers a bot's private chats only through the Bot API, so for a DM bot this SDK is the
/// only thing in the world that can report who its users are.</para>
/// </summary>
public class TelegramUserSnapshotExtractorTests
{
    private static User Alice(bool isBot = false) => new()
    {
        Id = 777,
        FirstName = "Alice",
        LastName = "Anderson",
        Username = "alice",
        LanguageCode = "en",
        IsPremium = true,
        IsBot = isBot
    };

    [Fact]
    public void Message_YieldsTheSenderWithEveryFieldTelegramGave()
    {
        var snapshot = TelegramUserSnapshotExtractor.From(new Update
        {
            Message = new Message { Id = 1, From = Alice(), Chat = new Chat { Id = 777, Type = ChatType.Private } }
        });

        Assert.NotNull(snapshot);
        Assert.Equal(777, snapshot!.TelegramUserId);
        Assert.Equal("alice", snapshot.Username);
        Assert.Equal("Alice", snapshot.FirstName);
        Assert.Equal("Anderson", snapshot.LastName);
        Assert.Equal("en", snapshot.LanguageCode);
        Assert.True(snapshot.IsPremium);
    }

    [Fact]
    public void CallbackQuery_YieldsTheUserWhoTapped()
    {
        // A button tap is frequently the ONLY thing a user ever does. Reading identity from messages
        // alone would leave a button-driven bot exactly as anonymous as before.
        var snapshot = TelegramUserSnapshotExtractor.From(new Update
        {
            CallbackQuery = new CallbackQuery { Id = "cb", From = Alice(), Data = "x" }
        });

        Assert.Equal(777, snapshot!.TelegramUserId);
    }

    [Fact]
    public void MembershipUpdate_YieldsTheUser()
    {
        // Someone blocking the bot is a user we may never see again — and often the last chance to
        // record who they were.
        var snapshot = TelegramUserSnapshotExtractor.From(new Update
        {
            MyChatMember = new ChatMemberUpdated
            {
                From = Alice(),
                Chat = new Chat { Id = 777, Type = ChatType.Private },
                OldChatMember = new ChatMemberMember { User = Alice() },
                NewChatMember = new ChatMemberBanned { User = Alice() }
            }
        });

        Assert.Equal(777, snapshot!.TelegramUserId);
    }

    [Fact]
    public void Bot_IsNeverReportedAsAUser()
    {
        var snapshot = TelegramUserSnapshotExtractor.From(new Update
        {
            Message = new Message { Id = 1, From = Alice(isBot: true), Chat = new Chat { Id = 5, Type = ChatType.Group } }
        });

        Assert.Null(snapshot);
    }

    [Fact]
    public void ChannelPostSignedByTheChannel_NamesNobody()
    {
        // No From at all. Inventing a user from the chat here would create one "person" per channel.
        var snapshot = TelegramUserSnapshotExtractor.From(new Update
        {
            ChannelPost = new Message { Id = 1, Chat = new Chat { Id = -1001234567890, Type = ChatType.Channel } }
        });

        Assert.Null(snapshot);
    }

    [Fact]
    public void UnknownUpdate_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(TelegramUserSnapshotExtractor.From(new Update()));
        Assert.Null(TelegramUserSnapshotExtractor.From(null));
    }

    [Fact]
    public void PrivateChat_IsAPerson_AndTheChatIdIsTheirUserId()
    {
        // The bot's own outgoing message carries the recipient's chat, and in a private chat that IS
        // the user: id, name and @username. For a bot whose users only tap buttons, this is often the
        // only identity that ever reaches us.
        var snapshot = TelegramUserSnapshotExtractor.OfPrivateChat(new Chat
        {
            Id = 777,
            Type = ChatType.Private,
            Username = "alice",
            FirstName = "Alice",
            LastName = "Anderson"
        });

        Assert.Equal(777, snapshot!.TelegramUserId);
        Assert.Equal("alice", snapshot.Username);
        Assert.Equal("Alice", snapshot.FirstName);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    [InlineData(ChatType.Channel)]
    public void NonPrivateChat_IsAPlaceNotAPerson(ChatType type)
    {
        // The single most damaging thing this code could do is record a room as a user: group ids are
        // negative, and a "user" with a negative id is a number nobody can explain.
        var snapshot = TelegramUserSnapshotExtractor.OfPrivateChat(new Chat
        {
            Id = -1001234567890,
            Type = type,
            Title = "A room"
        });

        Assert.Null(snapshot);
    }
}

/// <summary>
/// How identity reaches the wire. Each snapshot the server writes is a billed row for the customer, so
/// "one per person per batch" is a cost decision as much as a payload one.
/// </summary>
public class BufferedBotEventSenderUserTests
{
    private sealed class RecordingTransport : ITransport
    {
        public readonly List<BotEventsRequest> Requests = new();

        public Task<BotEventsResponse> SendTelegram(BotEventsRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(new BotEventsResponse());
        }
    }

    private static BotEvent Event() => new()
    {
        EventId = Guid.NewGuid(),
        Source = "tg",
        PlatformBotId = "bot",
        PlatformUserId = "777",
        EventOrigin = "platform",
        EventType = "message",
        EventName = "text_message",
        EventDate = DateTimeOffset.UtcNow
    };

    private static BotUserSnapshot Snapshot(string? username) =>
        new() { TelegramUserId = 777, Username = username, FirstName = "Alice" };

    private static BufferedBotEventSender Sender(RecordingTransport transport) =>
        new(transport, new BufferedBotEventSender.Options
        {
            BatchSize = 100,
            FlushInterval = TimeSpan.FromMilliseconds(50)
        });

    private static async Task<BotEventsRequest> FlushedAsync(RecordingTransport transport)
    {
        for (var i = 0; i < 100 && transport.Requests.Count == 0; i++)
            await Task.Delay(20);

        Assert.NotEmpty(transport.Requests);
        return transport.Requests[0];
    }

    [Fact]
    public async Task RepeatedSightingsOfOnePerson_CollapseToOneSnapshot_LatestWinning()
    {
        var transport = new RecordingTransport();
        await using var sender = Sender(transport);

        sender.TryEnqueue(Event(), Snapshot("alice"));
        sender.TryEnqueue(Event(), Snapshot("alice"));
        sender.TryEnqueue(Event(), Snapshot("alice_new"));

        var request = await FlushedAsync(transport);

        Assert.Equal(3, request.Events.Count);
        Assert.Single(request.Users!);
        Assert.Equal("alice_new", request.Users![0].Username);
    }

    [Fact]
    public async Task BatchThatNamesNobody_OmitsTheFieldEntirely()
    {
        // Older servers and quieter payloads both benefit: null is omitted from the JSON, so a batch
        // of anonymous events looks exactly as it did before this feature existed.
        var transport = new RecordingTransport();
        await using var sender = Sender(transport);

        sender.TryEnqueue(Event());

        var request = await FlushedAsync(transport);

        Assert.Null(request.Users);
    }

    [Fact]
    public async Task EnqueueUpdate_CarriesIdentityWithoutTheCallerDoingAnything()
    {
        var transport = new RecordingTransport();
        await using var sender = Sender(transport);

        var mapper = new TelegramUpdateToBotEventMapper("bot");

        sender.TryEnqueueUpdate(mapper, new Update
        {
            Id = 1,
            Message = new Message
            {
                Id = 10,
                Text = "hello",
                Date = DateTime.UtcNow,
                From = new User { Id = 777, FirstName = "Alice", Username = "alice" },
                Chat = new Chat { Id = 777, Type = ChatType.Private }
            }
        });

        var request = await FlushedAsync(transport);

        Assert.Single(request.Events);
        Assert.Equal("alice", request.Users!.Single().Username);
    }
}
