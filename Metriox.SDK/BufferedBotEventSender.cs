using Metriox.SDK.Transport;
using Metriox.SDK.Transport.Contracts;
using System.Threading.Channels;

namespace Metriox.SDK;

public sealed class BufferedBotEventSender : IAsyncDisposable
{
    public sealed class Options
    {
        public int Capacity { get; init; } = 10_000;
        public int BatchSize { get; init; } = 100;
        public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(10);
        public int SendRetries { get; init; } = 5;
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    }

    private readonly ITransport _transport;
    private readonly Options _opt;
    private readonly Action<string>? _log;
    private readonly Action<Exception, string>? _logError;

    /// <summary>
    /// One event and, when the update disclosed it, who caused it. They travel together through the
    /// buffer because they are sent together: the batch's user list must describe that batch.
    /// </summary>
    private readonly record struct QueuedItem(BotEvent Event, BotUserSnapshot? User);

    private readonly Channel<QueuedItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    public BufferedBotEventSender(
        ITransport transport,
        Options? options = null,
        Action<string>? log = null,
        Action<Exception, string>? logError = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _opt = options ?? new Options();
        _log = log;
        _logError = logError;

        _channel = Channel.CreateBounded<QueuedItem>(new BoundedChannelOptions(_opt.Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public bool TryEnqueue(BotEvent? e) => TryEnqueue(e, user: null);

    /// <summary>
    /// Queues an event together with the identity of the person who caused it.
    /// </summary>
    /// <param name="user">
    /// Who acted, from <c>TelegramUserSnapshotExtractor.From(update)</c>. Null is accepted and means
    /// "this update named nobody" — the event is still queued. Passing it is what stops the person
    /// showing up in Metriox as a bare numeric id; see <see cref="BotUserSnapshot"/>.
    /// </param>
    public bool TryEnqueue(BotEvent? e, BotUserSnapshot? user)
    {
        if (e is null) return false;

        var item = new QueuedItem(e, user);

        if (_channel.Writer.TryWrite(item))
            return true;

        if (_channel.Reader.TryRead(out _))
        {
            _channel.Writer.TryWrite(item);
            return false;
        }

        return false;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        var buffer = new List<QueuedItem>();
        var nextFlush = DateTimeOffset.UtcNow + _opt.FlushInterval;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (buffer.Count >= _opt.BatchSize)
                {
                    await FlushAsync(buffer, ct);
                    nextFlush = DateTimeOffset.UtcNow + _opt.FlushInterval;
                    continue;
                }

                var delay = nextFlush - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                var waitTask = reader.WaitToReadAsync(ct).AsTask();
                var delayTask = Task.Delay(delay, ct);

                var completed = await Task.WhenAny(waitTask, delayTask);

                if (completed == delayTask)
                {
                    if (buffer.Count > 0)
                        await FlushAsync(buffer, ct);

                    nextFlush = DateTimeOffset.UtcNow + _opt.FlushInterval;
                    continue;
                }

                if (!await waitTask)
                {
                    while (reader.TryRead(out var item))
                        buffer.Add(item);

                    if (buffer.Count > 0)
                        await FlushAsync(buffer, ct);

                    return;
                }

                while (reader.TryRead(out var item))
                    buffer.Add(item);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logError?.Invoke(ex, "Sender loop crashed.");
        }
    }

    private async Task FlushAsync(List<QueuedItem> buffer, CancellationToken ct)
    {
        while (buffer.Count > 0 && !ct.IsCancellationRequested)
        {
            var take = Math.Min(_opt.BatchSize, buffer.Count);
            var batch = buffer.GetRange(0, take);
            buffer.RemoveRange(0, take);

            var ok = await TrySendAsync(batch, ct);
            if (!ok)
                _log?.Invoke($"Dropped {batch.Count} events after retries.");
        }
    }

    private async Task<bool> TrySendAsync(List<QueuedItem> batch, CancellationToken ct)
    {
        Exception? last = null;

        for (int i = 0; i <= _opt.SendRetries; i++)
        {
            try
            {
                var req = new BotEventsRequest
                {
                    Events = batch.ConvertAll(x => x.Event),
                    Users = DistinctUsers(batch)
                };

                await _transport.SendTelegram(req, ct);

                _log?.Invoke($"Sent {req.Events.Count} events, {req.Users?.Count ?? 0} users.");
                _log?.Invoke($"Sender opts: Capacity={_opt.Capacity}, BatchSize={_opt.BatchSize}, FlushInterval={_opt.FlushInterval}");

                
                return true;
            }
            catch (Exception ex) when (i < _opt.SendRetries)
            {
                last = ex;
                _logError?.Invoke(ex, $"Send failed, retry {i + 1}/{_opt.SendRetries}");
                if (_opt.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(_opt.RetryDelay, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        if (last != null)
            _logError?.Invoke(last, "Send failed permanently.");

        return false;
    }

    /// <summary>
    /// One entry per person in the batch, latest sighting winning.
    ///
    /// <para>A chatty user appears on every event they caused; sending twenty identical copies of their
    /// name would be twenty rows for the server to write and pay for. The latest one wins because
    /// within a single batch it is the freshest: someone who changed their @username mid-batch must not
    /// be recorded under the old one.</para>
    /// </summary>
    private static List<BotUserSnapshot>? DistinctUsers(List<QueuedItem> batch)
    {
        Dictionary<long, BotUserSnapshot>? byId = null;

        foreach (var item in batch)
        {
            if (item.User is null) continue;

            byId ??= new Dictionary<long, BotUserSnapshot>();
            byId[item.User.TelegramUserId] = item.User;
        }

        // Null rather than an empty list: the field is omitted from the payload entirely when this
        // batch told us nothing about anybody.
        return byId is null ? null : new List<BotUserSnapshot>(byId.Values);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _pumpTask; } catch { }
        _cts.Dispose();
    }
}
