using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Push;

namespace YueUI.Api.Tests;

/// <summary>The real sender against a scripted push service: VAPID header, encryption and the service's answers.</summary>
public sealed class WebPushSenderTests : IDisposable
{
    // A subscription as Chrome makes it (key pair of the Web Push test vectors in RFC 8291).
    private static readonly PushSubscriptionEntry Subscription = new(
        "https://fcm.googleapis.com/fcm/send/abc",
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4",
        "BTBZMqHH6r4Tts7J_aSIgg",
        "de",
        DateTimeOffset.UnixEpoch);

    private readonly string _root = Directory.CreateTempSubdirectory("yueui-push-").FullName;
    private readonly List<HttpRequestMessage> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.Created;

    [Fact]
    public async Task A_message_goes_out_encrypted_with_a_VAPID_token_of_this_servers_key()
    {
        var (sender, store) = Create();

        Assert.Equal(PushResult.Delivered, await sender.SendAsync(Subscription, """{"title": "Song fertig"}""", CancellationToken.None));

        var request = Assert.Single(_requests);
        Assert.Equal(Subscription.Endpoint, request.RequestUri!.ToString());
        Assert.Equal("vapid", request.Headers.Authorization!.Scheme);
        Assert.Contains($"k={store.PublicKey}", request.Headers.Authorization.Parameter);
        Assert.Equal("aes128gcm", Assert.Single(request.Content!.Headers.ContentEncoding));
        Assert.Equal("43200", Assert.Single(request.Headers.GetValues("TTL")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, PushResult.Gone)]
    [InlineData(HttpStatusCode.NotFound, PushResult.Gone)]
    [InlineData(HttpStatusCode.BadRequest, PushResult.Failed)]
    public async Task The_services_refusal_tells_whether_the_subscription_is_gone(HttpStatusCode status, PushResult expected)
    {
        _status = status;
        var (sender, _) = Create();

        Assert.Equal(expected, await sender.SendAsync(Subscription, "{}", CancellationToken.None));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (WebPushSender Sender, PushStore Store) Create()
    {
        var options = Options.Create(new PushOptions { DataPath = Path.Combine(_root, "push.json") });
        var store = new PushStore(options, TimeProvider.System, NullLogger<PushStore>.Instance);
        var sender = new WebPushSender(new Clients(new Handler(this)), store, options, NullLogger<WebPushSender>.Instance);
        return (sender, store);
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(WebPushSenderTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read now; the content is gone once the client is done with the request.
            await request.Content!.LoadIntoBufferAsync(cancellationToken);
            test._requests.Add(request);
            return new HttpResponseMessage(test._status);
        }
    }
}
