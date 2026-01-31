using Metriox.SDK.Transport.Contracts;

namespace Metriox.SDK.Transport;

public interface ITransport
{
    Task<BotEventsResponse> SendTelegram(BotEventsRequest request, CancellationToken cancellationToken = default);
}