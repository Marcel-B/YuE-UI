using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Push;

/// <summary>A browser that asked to be notified; <see cref="Language"/> picks the notification texts.</summary>
public sealed record PushSubscriptionEntry(string Endpoint, string P256dh, string Auth, string Language, DateTimeOffset CreatedAt);

/// <summary>
/// This server's VAPID key pair and the subscriptions, kept in one small JSON file (<see cref="PushOptions.DataPath"/>).
/// </summary>
/// <remarks>
/// The key pair is made on first use and must stay: every subscription is bound to the public key it was made with,
/// so a new pair would silently orphan all of them. The file holds the private key, so only its owner may read it.
/// </remarks>
public sealed class PushStore(IOptions<PushOptions> options, TimeProvider time, ILogger<PushStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _path = options.Value.ResolvedDataPath;
    private Data? _data;

    /// <summary>The VAPID public key as browsers take it for <c>applicationServerKey</c> (uncompressed point, base64url).</summary>
    public string PublicKey
    {
        get
        {
            lock (_gate)
            {
                return LoadLocked().PublicKey;
            }
        }
    }

    public string PrivateKey
    {
        get
        {
            lock (_gate)
            {
                return LoadLocked().PrivateKey;
            }
        }
    }

    public IReadOnlyList<PushSubscriptionEntry> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. LoadLocked().Subscriptions];
            }
        }
    }

    /// <summary>Adds a subscription, or updates its keys and language when the browser subscribes again.</summary>
    public void Add(string endpoint, string p256dh, string auth, string language)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            var entry = new PushSubscriptionEntry(endpoint, p256dh, auth, language, time.GetUtcNow());
            data.Subscriptions = [.. data.Subscriptions.Where(s => s.Endpoint != endpoint), entry];
            SaveLocked(data);
        }
    }

    /// <returns>False when there was no such subscription.</returns>
    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            var remaining = data.Subscriptions.Where(s => s.Endpoint != endpoint).ToList();
            if (remaining.Count == data.Subscriptions.Count)
            {
                return false;
            }
            data.Subscriptions = remaining;
            SaveLocked(data);
            return true;
        }
    }

    public PushSubscriptionEntry? Find(string endpoint)
    {
        lock (_gate)
        {
            return LoadLocked().Subscriptions.FirstOrDefault(s => s.Endpoint == endpoint);
        }
    }

    private Data LoadLocked()
    {
        if (_data is not null)
        {
            return _data;
        }
        if (File.Exists(_path))
        {
            try
            {
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json);
            }
            catch (JsonException exception)
            {
                // Starting over loses the subscriptions; the browsers see notifications stop and can switch them on again.
                logger.LogError(exception, "Could not read {Path}; starting with a new key pair", _path);
            }
        }
        if (_data is not { PublicKey.Length: > 0, PrivateKey.Length: > 0 })
        {
            _data = NewKeyPair();
            SaveLocked(_data);
        }
        return _data;
    }

    private void SaveLocked(Data data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Written next to the file and moved over it, so a crash never leaves half a file (and no key pair).
        var temp = _path + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(temp, fileOptions))
        {
            JsonSerializer.Serialize(stream, data, Json);
        }
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>A P-256 key pair as the VAPID spec (RFC 8292) and the Push API expect it.</summary>
    private static Data NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        byte[] publicKey = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
        return new Data
        {
            PublicKey = Base64Url.EncodeToString(publicKey),
            PrivateKey = Base64Url.EncodeToString(parameters.D!),
        };
    }

    private sealed class Data
    {
        public string PublicKey { get; set; } = "";

        public string PrivateKey { get; set; } = "";

        public List<PushSubscriptionEntry> Subscriptions { get; set; } = [];
    }
}
