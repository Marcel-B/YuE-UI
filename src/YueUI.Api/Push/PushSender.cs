using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Push;

public enum PushResult
{
    Delivered,

    /// <summary>The push service no longer knows the subscription (404/410): the browser unsubscribed or reset.</summary>
    Gone,

    Failed,
}

/// <summary>Hands one message to the push service of a subscription (Apple's, Google's, Mozilla's …).</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(PushSubscriptionEntry subscription, string payload, CancellationToken cancellationToken);
}

/// <summary>Web Push with VAPID and aes128gcm (RFC 8291/8292), through Lib.Net.Http.WebPush.</summary>
public sealed class WebPushSender(
    IHttpClientFactory httpClients,
    PushStore store,
    IOptions<PushOptions> options,
    ILogger<WebPushSender> logger) : IPushSender
{
    public const string HttpClientName = "push";

    /// <summary>A song that finished while the phone was off is still worth hearing about the next morning, not after that.</summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(12);

    public async Task<PushResult> SendAsync(PushSubscriptionEntry subscription, string payload, CancellationToken cancellationToken)
    {
        var client = new PushServiceClient(httpClients.CreateClient(HttpClientName));
        var target = new PushSubscription { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        using var vapid = new VapidAuthentication(store.PublicKey, store.PrivateKey) { Subject = options.Value.Subject };
        var message = new PushMessage(payload) { TimeToLive = (int)TimeToLive.TotalSeconds, Urgency = PushMessageUrgency.Normal };
        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, vapid, cancellationToken);
            return PushResult.Delivered;
        }
        catch (PushServiceClientException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushResult.Gone;
        }
        catch (Exception exception) when (exception is PushServiceClientException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Push to {Host} failed", new Uri(subscription.Endpoint).Host);
            return PushResult.Failed;
        }
    }
}
