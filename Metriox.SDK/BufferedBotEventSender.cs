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

    private readonly Channel<BotEvent> _channel;
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

        _channel = Channel.CreateBounded<BotEvent>(new BoundedChannelOptions(_opt.Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public bool TryEnqueue(BotEvent? e)
    {
        if (e is null) return false;

        if (_channel.Writer.TryWrite(e))
            return true;
        
        if (_channel.Reader.TryRead(out _))
        {
            _channel.Writer.TryWrite(e);
            return false;
        }

        return false;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        var buffer = new List<BotEvent>();
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

    private async Task FlushAsync(List<BotEvent> buffer, CancellationToken ct)
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

    private async Task<bool> TrySendAsync(List<BotEvent> batch, CancellationToken ct)
    {
        Exception? last = null;

        for (int i = 0; i <= _opt.SendRetries; i++)
        {
            try
            {
                var req = new BotEventsRequest
                {
                    Events = batch
                };

                await _transport.SendTelegram(req, ct);
                
                _log?.Invoke($"Sent {req.Events.Count} events.");
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

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _pumpTask; } catch { }
        _cts.Dispose();
    }
}
