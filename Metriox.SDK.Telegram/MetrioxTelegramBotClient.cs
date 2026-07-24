using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using TGFile = Telegram.Bot.Types.TGFile;

namespace Metriox.SDK.Telegram;

/// <summary>
/// Wraps an <see cref="ITelegramBotClient"/> so the bot's OWN messages reach Metriox automatically.
///
/// <para><b>Why this exists.</b> The Bot API never reports a bot's own sends back to it — there is no
/// update for "I sent a message" — and Metriox's MTProto worker cannot see private-chat traffic. So for
/// a Bot-API bot, the bot's half of every conversation simply does not exist as data: transcripts render
/// every bubble as the user's, because the only messages anyone ever captured were the user's. Nothing
/// about how direction is stored can fix that; the send has to be reported by the only party that
/// observes it, which is the bot itself.</para>
///
/// <para><b>Why it wraps SendRequest.</b> Every Bot-API method that produces a message — SendMessage,
/// SendPhoto, SendDocument, EditMessageText, CopyMessage, … — returns a <see cref="Message"/> through the
/// one <c>SendRequest</c> entry point. Intercepting there covers all of them, including methods added by
/// future Telegram.Bot versions, instead of a per-method wrapper list that silently misses whatever the
/// bot actually calls.</para>
///
/// <para>Failure is always silent. Analytics must never break a bot: if capture throws, the send has
/// already succeeded and its result is returned unchanged.</para>
///
/// <example>
/// <code>
/// var bot = new TelegramBotClient(token).WithMetrioxCapture(sender, "mybot");
/// await bot.SendMessage(chatId, "hi");   // captured, no extra call
/// </code>
/// </example>
/// </summary>
public sealed class MetrioxTelegramBotClient : ITelegramBotClient
{
    private readonly ITelegramBotClient _inner;
    private readonly BufferedBotEventSender _sender;
    private readonly string _platformBotId;
    private readonly Action<Exception, string>? _logError;

    public MetrioxTelegramBotClient(
        ITelegramBotClient inner,
        BufferedBotEventSender sender,
        string platformBotId,
        Action<Exception, string>? logError = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));

        if (string.IsNullOrEmpty(platformBotId))
            throw new ArgumentException("platformBotId is required.", nameof(platformBotId));

        _platformBotId = platformBotId;
        _logError = logError;
    }

    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var response = await _inner.SendRequest(request, cancellationToken).ConfigureAwait(false);

        // Only message-producing calls are of interest; everything else (answerCallbackQuery, getChat,
        // setMyCommands …) passes through untouched.
        if (response is Message sent) TryCapture(sent);

        return response;
    }

    private void TryCapture(Message sent)
    {
        try
        {
            // A send with no chat is not something a transcript can place.
            if (sent.Chat is null) return;

            _sender.TryEnqueue(TelegramOutgoingMessageMapper.ToBotEvent(sent, _platformBotId));
        }
        catch (Exception ex)
        {
            // The send already succeeded. Losing one analytics event is strictly better than surfacing an
            // analytics failure as a failed send.
            _logError?.Invoke(ex, "Metriox: failed to capture an outgoing Telegram message");
        }
    }

    // ---- Pass-through surface ----

    public bool LocalBotServer => _inner.LocalBotServer;

    public long BotId => _inner.BotId;

    public TimeSpan Timeout
    {
        get => _inner.Timeout;
        set => _inner.Timeout = value;
    }

    public IExceptionParser ExceptionsParser
    {
        get => _inner.ExceptionsParser;
        set => _inner.ExceptionsParser = value;
    }

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add => _inner.OnMakingApiRequest += value;
        remove => _inner.OnMakingApiRequest -= value;
    }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add => _inner.OnApiResponseReceived += value;
        remove => _inner.OnApiResponseReceived -= value;
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default)
        => _inner.TestApi(cancellationToken);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => _inner.DownloadFile(filePath, destination, cancellationToken);

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => _inner.DownloadFile(file, destination, cancellationToken);
}

public static class MetrioxTelegramBotClientExtensions
{
    /// <summary>
    /// Returns a client that reports the bot's own sends to Metriox. Use the returned client everywhere
    /// the bot sends messages — anything still holding the original client is not captured.
    /// </summary>
    /// <param name="client">The real client (e.g. <c>new TelegramBotClient(token)</c>).</param>
    /// <param name="sender">The buffered sender events are queued on.</param>
    /// <param name="platformBotId">Your bot identifier (username or numeric id as a string).</param>
    /// <param name="logError">Optional sink for capture failures; capture never throws either way.</param>
    public static ITelegramBotClient WithMetrioxCapture(
        this ITelegramBotClient client,
        BufferedBotEventSender sender,
        string platformBotId,
        Action<Exception, string>? logError = null)
        => new MetrioxTelegramBotClient(client, sender, platformBotId, logError);
}
