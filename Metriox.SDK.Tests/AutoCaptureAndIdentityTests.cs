using Metriox.SDK;
using Metriox.SDK.Telegram;
using Metriox.SDK.Telegram.Mappers;
using Metriox.SDK.Transport;
using Metriox.SDK.Transport.Contracts;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Metriox.SDK.Tests;

/// <summary>
/// Event identity is a cross-repo contract: the id this SDK mints for a message must equal the one
/// Metriox's MTProto worker mints for the same message, or a dual-captured event is two rows forever.
/// </summary>
public class TelegramEventIdentityTests
{
    private static Message Sent(int id = 555, long chatId = 312302365, DateTime? editDate = null) => new()
    {
        Id = id,
        Chat = new Chat { Id = chatId, Type = ChatType.Private },
        Date = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
        EditDate = editDate,
        Text = "hello"
    };

    [Fact]
    public void OutgoingMessage_IdIsDerivedFromChatAndMessageId_NotFromTheBotId()
    {
        // Hand-computed from the shared key format. If this ever needs changing, the backend's
        // TelegramEventKeys changed too — and every previously-written id stops matching.
        var expected = TelegramEventIdentity.Uuid5("tg:msg:312302365:555");

        Assert.Equal(expected, TelegramOutgoingMessageMapper.ToBotEvent(Sent(), "mybot").EventId);
    }

    [Fact]
    public void OutgoingId_DoesNotDependOnThePlatformBotId()
    {
        // The bot identifier is deliberately absent from the key: the server keys on its own internal bot
        // id, which no SDK can know, so any key containing one is unmatchable by construction.
        var a = TelegramOutgoingMessageMapper.ToBotEvent(Sent(), "botA").EventId;
        var b = TelegramOutgoingMessageMapper.ToBotEvent(Sent(), "botB").EventId;

        Assert.Equal(a, b);
    }

    [Fact]
    public void EditedSend_GetsItsOwnId_SoItDoesNotCollapseOntoTheOriginal()
    {
        var original = TelegramOutgoingMessageMapper.ToBotEvent(Sent(), "mybot").EventId;
        var edited = TelegramOutgoingMessageMapper
            .ToBotEvent(Sent(editDate: new DateTime(2026, 7, 25, 12, 5, 0, DateTimeKind.Utc)), "mybot").EventId;

        Assert.NotEqual(original, edited);
    }

    [Fact]
    public void InboundUpdate_IsKeyedOnChatAndMessage_NotUpdateId()
    {
        // Two deliveries of the same message with different update_ids (a webhook retry, or polling after
        // a restart) are ONE event. Under the old tg:update:{bot}:{update_id} key they were two.
        var first = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(new Update { Id = 1, Message = Sent() }, "mybot");
        var second = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(new Update { Id = 2, Message = Sent() }, "mybot");

        Assert.Equal(first!.EventId, second!.EventId);
        Assert.Equal(TelegramEventIdentity.Uuid5("tg:msg:312302365:555"), first.EventId);
    }

    [Fact]
    public void UpdateWithNoSharedCoordinate_FallsBackWithoutThrowing()
    {
        // Membership/boost/business-connection updates have no coordinate MTProto also sees. They keep an
        // update-id key: idempotency for this producer only, which is all that is available.
        Assert.Null(TelegramEventIdentity.ForUpdate(new Update { Id = 9 }));
    }

    [Fact]
    public void Direction_IsStampedOnBothSides()
    {
        Assert.Equal("outbound", TelegramOutgoingMessageMapper.ToBotEvent(Sent(), "mybot").PropsString?["tg.direction"]);

        var inbound = new TelegramUpdateToBotEventMapper("mybot").ToBotEvent(new Update { Id = 1, Message = Sent() }, "mybot");
        Assert.Equal("inbound", inbound!.PropsString?["tg.direction"]);
    }
}

public class MessageEntitySerializerTests
{
    [Fact]
    public void SerializesTypeOffsetLengthAndUrl_InBotApiTokens()
    {
        var json = MessageEntitySerializer.ToCompactJson(new[]
        {
            new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 5 },
            new MessageEntity { Type = MessageEntityType.TextLink, Offset = 6, Length = 4, Url = "https://metriox.com" }
        });

        Assert.Equal("[{\"type\":\"bold\",\"offset\":0,\"length\":5},{\"type\":\"text_link\",\"offset\":6,\"length\":4,\"url\":\"https://metriox.com\"}]", json);
    }

    [Fact]
    public void NoEntities_IsNull_SoPlainMessagesCarryNoProp()
    {
        Assert.Null(MessageEntitySerializer.ToCompactJson(null));
        Assert.Null(MessageEntitySerializer.ToCompactJson(Array.Empty<MessageEntity>()));
    }

    [Fact]
    public void DegenerateSpansAreDropped_AndAnAllBadSetIsNull()
    {
        Assert.Null(MessageEntitySerializer.ToCompactJson(new[]
        {
            new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 0 }
        }));
    }

    [Fact]
    public void SpanCountIsCapped()
    {
        var many = Enumerable.Range(0, MessageEntitySerializer.MaxSpans + 10)
            .Select(i => new MessageEntity { Type = MessageEntityType.Mention, Offset = i, Length = 1 })
            .ToArray();

        var json = MessageEntitySerializer.ToCompactJson(many)!;

        Assert.Equal(MessageEntitySerializer.MaxSpans, json.Split("{\"type\"").Length - 1);
    }

    [Fact]
    public void OutgoingMessage_CarriesItsEntities()
    {
        var sent = new Message
        {
            Id = 1,
            Chat = new Chat { Id = 1, Type = ChatType.Private },
            Text = "bold text",
            Entities = new[] { new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 4 } }
        };

        var ev = TelegramOutgoingMessageMapper.ToBotEvent(sent, "mybot");

        Assert.Equal("[{\"type\":\"bold\",\"offset\":0,\"length\":4}]", ev.PropsString?["tg.entities"]);
    }
}

/// <summary>
/// The auto-capturing client is what makes a bot's own half of a conversation exist at all: the Bot API
/// never reports a bot's sends, so without this every transcript is one-sided no matter how direction is
/// stored.
/// </summary>
public class MetrioxTelegramBotClientTests
{
    private sealed class RecordingTransport : ITransport
    {
        public readonly List<BotEvent> Received = new();

        public Task<BotEventsResponse> SendTelegram(BotEventsRequest request, CancellationToken cancellationToken = default)
        {
            lock (Received) Received.AddRange(request.Events ?? Enumerable.Empty<BotEvent>());
            return Task.FromResult(new BotEventsResponse());
        }
    }

    /// <summary>Minimal stand-in: returns a canned response from SendRequest and stubs the rest.</summary>
    private sealed class FakeBotClient : ITelegramBotClient
    {
        private readonly object? _response;
        public int Calls { get; private set; }

        public FakeBotClient(object? response) => _response = response;

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult((TResponse)_response!);
        }

        public bool LocalBotServer => false;
        public long BotId => 42;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;

        // Silence "never used" for the pass-through events, which exist only to satisfy the interface.
        internal void Unused() { OnMakingApiRequest?.Invoke(this, null!, default); OnApiResponseReceived?.Invoke(this, null!, default); }
    }

    private static (ITelegramBotClient client, RecordingTransport transport, BufferedBotEventSender sender) Build(object? response)
    {
        var transport = new RecordingTransport();
        var sender = new BufferedBotEventSender(transport, new BufferedBotEventSender.Options
        {
            BatchSize = 1,
            FlushInterval = TimeSpan.FromMilliseconds(20)
        });

        return (new FakeBotClient(response).WithMetrioxCapture(sender, "mybot"), transport, sender);
    }

    private static async Task<List<BotEvent>> DrainAsync(RecordingTransport transport)
    {
        // The sender flushes on its own timer; poll briefly rather than sleeping a fixed span.
        for (var i = 0; i < 100; i++)
        {
            lock (transport.Received)
                if (transport.Received.Count > 0) return transport.Received.ToList();

            await Task.Delay(20);
        }

        return new List<BotEvent>();
    }

    [Fact]
    public async Task ASendIsCaptured_WithNoExtraCallFromTheBot()
    {
        var sent = new Message
        {
            Id = 777,
            Chat = new Chat { Id = 312302365, Type = ChatType.Private },
            Text = "hi from the bot"
        };

        var (client, transport, sender) = Build(sent);
        await using (sender)
        {
            await client.SendRequest(new FakeRequest<Message>());

            var captured = await DrainAsync(transport);

            var one = Assert.Single(captured);
            Assert.Equal("outbound", one.PropsString?["tg.direction"]);
            Assert.Equal("hi from the bot", one.Text);
            Assert.Equal(TelegramEventIdentity.Uuid5("tg:msg:312302365:777"), one.EventId);
        }
    }

    [Fact]
    public async Task NonMessageResponsesArePassedThroughUncaptured()
    {
        // answerCallbackQuery, setMyCommands, getChat … must not produce transcript rows.
        var (client, transport, sender) = Build(true);
        await using (sender)
        {
            var result = await client.SendRequest(new FakeRequest<bool>());

            Assert.True(result);
            await Task.Delay(80);
            lock (transport.Received) Assert.Empty(transport.Received);
        }
    }

    private sealed class FakeRequest<T> : IRequest<T>
    {
        public HttpMethod HttpMethod => HttpMethod.Post;
        public string MethodName => "fake";
        public bool IsWebhookResponse { get; set; }
        public HttpContent? ToHttpContent() => null;
    }
}
