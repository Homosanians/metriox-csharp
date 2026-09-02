using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types;

namespace Metriox.SDK.Telegram;

/// <summary>
/// The one-call way to report a Telegram update: the event and the person who caused it, together.
/// </summary>
public static class BufferedBotEventSenderExtensions
{
    /// <summary>
    /// Maps <paramref name="update"/> to an event and queues it along with the sender's identity.
    ///
    /// <para>This is what a bot's update loop should call. The two-step form —
    /// <c>sender.TryEnqueue(mapper.ToBotEvent(update))</c> — still works and still records the event,
    /// but it discards the <c>from</c> object Telegram attached, and those users then appear in
    /// Metriox as bare numeric ids with no name and no @username.</para>
    /// </summary>
    /// <returns>False when the update type is excluded by the mapper, or the queue rejected it.</returns>
    public static bool TryEnqueueUpdate(
        this BufferedBotEventSender sender,
        TelegramUpdateToBotEventMapper mapper,
        Update update,
        string? platformBotIdOverride = null,
        DateTimeOffset? receivedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(update);

        var e = mapper.ToBotEvent(update, platformBotIdOverride, receivedAtUtc);
        if (e is null) return false;

        return sender.TryEnqueue(e, TelegramUserSnapshotExtractor.From(update));
    }
}
