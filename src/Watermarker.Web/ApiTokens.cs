using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watermarker.Web;

/// <summary>
/// Token di accesso alle API, creato da un amministratore per un'applicazione.
/// Del segreto si conserva solo l'impronta SHA-256: chi legge il file non può usare i token.
/// </summary>
public sealed class ApiToken
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Contact { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? ExpiresAt { get; set; }
    public ApiTokenSettings Settings { get; set; } = new();

    public string SecretHash { get; set; } = "";
    public DateTimeOffset SecretCreatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedFrom { get; set; }
    public long UsageCount { get; set; }

    [JsonIgnore] public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.UtcNow;
    [JsonIgnore] public string Status => !Enabled ? "disabled" : IsExpired ? "expired" : "active";

    /// <summary>Parte visibile del token, utile per riconoscerlo senza mostrarne il segreto.</summary>
    [JsonIgnore] public string Hint => $"{ApiTokenStore.Prefix}{Id}_…";

    /// <summary>Dati mostrati nella pagina di amministrazione (senza impronta del segreto).</summary>
    public object ToView() => new
    {
        id = Id,
        name = Name,
        description = Description,
        contact = Contact,
        enabled = Enabled,
        status = Status,
        hint = Hint,
        expiresAt = ExpiresAt,
        settings = Settings,
        secretCreatedAt = SecretCreatedAt,
        createdAt = CreatedAt,
        createdBy = CreatedBy,
        updatedAt = UpdatedAt,
        updatedBy = UpdatedBy,
        lastUsedAt = LastUsedAt,
        lastUsedFrom = LastUsedFrom,
        usageCount = UsageCount,
    };
}

/// <summary>Cosa può fare un token. I valori null usano i default della sezione "Api".</summary>
public sealed class ApiTokenSettings
{
    public bool AllowSync { get; set; } = true;
    public bool AllowAsync { get; set; } = true;

    /// <summary>Dimensione massima del file, in MB (non oltre Portal:MaxUploadMb).</summary>
    public int? MaxUploadMb { get; set; }

    /// <summary>Richieste di elaborazione al minuto.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>Lavori asincroni non ancora terminati.</summary>
    public int? MaxActiveJobs { get; set; }

    /// <summary>Testo imposto: se presente, il testo inviato dal chiamante viene ignorato.</summary>
    public string? FixedText { get; set; }

    /// <summary>Il chiamante può indicare l'utente finale ({user} ed {email}). Altrimenti {user} è il nome del token.</summary>
    public bool AllowUserOverride { get; set; }

    /// <summary>Reti o indirizzi da cui il token è accettato (es. 100.64.0.0/10). Vuoto = da qualsiasi indirizzo.</summary>
    public List<string> AllowedNetworks { get; set; } = [];
}

/// <summary>Dati inviati dalla pagina di amministrazione per creare o modificare un token.</summary>
public sealed class ApiTokenInput
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Contact { get; set; }
    public bool? Enabled { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public ApiTokenSettings? Settings { get; set; }
}

/// <summary>
/// Archivio dei token in un file JSON. Pochi token, poche modifiche: un file con scrittura atomica
/// basta e si salva insieme al volume dei dati. L'uso (ultimo accesso, contatore) è aggiornato in memoria
/// e scritto periodicamente da <see cref="ApiMaintenance"/>.
/// </summary>
public sealed class ApiTokenStore
{
    public const string Prefix = "wmk_";
    private const int IdLength = 16;     // 8 byte in esadecimale
    private const int SecretLength = 64; // 32 byte in esadecimale

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly Dictionary<string, ApiToken> _tokens = new(StringComparer.Ordinal);
    private readonly string? _file;
    private readonly ILogger<ApiTokenStore> _log;
    private bool _dirty;

    public ApiTokenStore(string? dataPath, ILogger<ApiTokenStore> log)
    {
        _log = log;
        if (string.IsNullOrWhiteSpace(dataPath) || !Storage.IsWritable(dataPath))
        {
            log.LogWarning("API token folder '{Path}' is not writable: tokens are kept in memory and lost at restart.", dataPath);
            return;
        }

        _file = Path.Combine(dataPath, "tokens.json");
        if (!File.Exists(_file)) return;

        var list = JsonSerializer.Deserialize<List<ApiToken>>(File.ReadAllText(_file), Json) ?? [];
        foreach (var t in list) _tokens[t.Id] = t;
        log.LogInformation("Loaded {Count} API tokens.", _tokens.Count);
    }

    public bool IsPersistent => _file is not null;

    public IReadOnlyList<ApiToken> List()
    {
        lock (_gate) return _tokens.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(Clone).ToList();
    }

    public ApiToken? Get(string id)
    {
        lock (_gate) return _tokens.TryGetValue(id, out var t) ? Clone(t) : null;
    }

    /// <summary>Crea un token. Il valore completo viene restituito solo qui e non è più recuperabile.</summary>
    public (ApiToken Token, string Value) Create(ApiTokenInput input, string? by)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(IdLength / 2)).ToLowerInvariant();
        var (secret, hash) = NewSecret();

        var token = new ApiToken
        {
            Id = id,
            Name = input.Name!.Trim(),
            Description = Clean(input.Description),
            Contact = Clean(input.Contact),
            Enabled = input.Enabled ?? true,
            ExpiresAt = input.ExpiresAt,
            Settings = Normalize(input.Settings),
            SecretHash = hash,
            SecretCreatedAt = now,
            CreatedAt = now,
            CreatedBy = by,
        };

        lock (_gate)
        {
            _tokens[id] = token;
            SaveLocked();
            return (Clone(token), $"{Prefix}{id}_{secret}");
        }
    }

    public ApiToken? Update(string id, ApiTokenInput input, string? by)
    {
        lock (_gate)
        {
            if (!_tokens.TryGetValue(id, out var t)) return null;
            t.Name = input.Name!.Trim();
            t.Description = Clean(input.Description);
            t.Contact = Clean(input.Contact);
            t.Enabled = input.Enabled ?? t.Enabled;
            t.ExpiresAt = input.ExpiresAt;
            t.Settings = Normalize(input.Settings);
            t.UpdatedAt = DateTimeOffset.UtcNow;
            t.UpdatedBy = by;
            SaveLocked();
            return Clone(t);
        }
    }

    /// <summary>Sostituisce il segreto: il valore precedente smette subito di funzionare.</summary>
    public (ApiToken Token, string Value)? Rotate(string id, string? by)
    {
        var (secret, hash) = NewSecret();
        lock (_gate)
        {
            if (!_tokens.TryGetValue(id, out var t)) return null;
            t.SecretHash = hash;
            t.SecretCreatedAt = DateTimeOffset.UtcNow;
            t.UpdatedAt = t.SecretCreatedAt;
            t.UpdatedBy = by;
            SaveLocked();
            return (Clone(t), $"{Prefix}{id}_{secret}");
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_tokens.Remove(id)) return false;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Verifica il token presentato. In caso di rifiuto <paramref name="failure"/> spiega il motivo.</summary>
    public ApiToken? Authenticate(string presented, out string failure)
    {
        failure = "The API token is not valid.";
        presented = presented.Trim();

        if (presented.Length != Prefix.Length + IdLength + 1 + SecretLength
            || !presented.StartsWith(Prefix, StringComparison.Ordinal)
            || presented[Prefix.Length + IdLength] != '_')
            return null;

        var id = presented.Substring(Prefix.Length, IdLength);
        var secret = presented[(Prefix.Length + IdLength + 1)..];

        ApiToken? token;
        lock (_gate) token = _tokens.TryGetValue(id, out var t) ? Clone(t) : null;

        // Il confronto avviene anche se il token non esiste, per non rivelarlo dal tempo di risposta.
        var expected = Convert.FromHexString(token?.SecretHash ?? new string('0', SecretLength));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        if (token is null || !CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        if (!token.Enabled) { failure = "The API token has been disabled."; return null; }
        if (token.IsExpired) { failure = $"The API token expired on {token.ExpiresAt:yyyy-MM-dd}."; return null; }
        return token;
    }

    /// <summary>Registra l'utilizzo (scritto su disco al giro successivo di manutenzione).</summary>
    public void Touch(string id, string? from)
    {
        lock (_gate)
        {
            if (!_tokens.TryGetValue(id, out var t)) return;
            t.LastUsedAt = DateTimeOffset.UtcNow;
            t.LastUsedFrom = from;
            t.UsageCount++;
            _dirty = true;
        }
    }

    public void Flush()
    {
        lock (_gate) if (_dirty) SaveLocked();
    }

    /// <summary>Controlla i dati inviati dalla pagina di amministrazione. Restituisce null se sono validi.</summary>
    public static string? Validate(ApiTokenInput input, PortalOptions portal, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Enter a name for the token.";
        if (input.Name.Trim().Length > 100) return "The name can be at most 100 characters long.";
        if (input.Description?.Length > 500) return "The description can be at most 500 characters long.";
        if (input.Contact?.Length > 200) return "The contact can be at most 200 characters long.";
        if (isNew && input.ExpiresAt is { } e && e <= DateTimeOffset.UtcNow) return "The expiry date must be in the future.";

        var s = input.Settings ?? new ApiTokenSettings();
        if (!s.AllowSync && !s.AllowAsync) return "Allow at least one mode (synchronous or asynchronous).";
        if (s.MaxUploadMb is { } mb && (mb < 1 || mb > portal.MaxUploadMb))
            return $"The maximum file size must be between 1 and {portal.MaxUploadMb} MB.";
        if (s.RequestsPerMinute is { } rpm && (rpm < 1 || rpm > 10_000)) return "Requests per minute must be between 1 and 10000.";
        if (s.MaxActiveJobs is { } jobs && (jobs < 1 || jobs > 1000)) return "Active jobs must be between 1 and 1000.";
        if (s.FixedText?.Trim().Length > portal.MaxTextLength) return $"The fixed text can be at most {portal.MaxTextLength} characters long.";

        foreach (var n in s.AllowedNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
            if (!TryParseNetwork(n, out _))
                return $"'{n}' is not a valid address or network (examples: 100.101.102.103, 100.64.0.0/10).";

        return null;
    }

    /// <summary>Indirizzo singolo o rete in notazione CIDR.</summary>
    public static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
    {
        value = value.Trim();
        if (!value.Contains('/') && IPAddress.TryParse(value, out var ip))
            value += ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32";
        return System.Net.IPNetwork.TryParse(value, out network);
    }

    public static bool IsAllowedFrom(ApiToken token, IPAddress? remote)
    {
        var nets = token.Settings.AllowedNetworks;
        if (nets.Count == 0) return true;
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return nets.Any(n => TryParseNetwork(n, out var net) && net.Contains(remote));
    }

    private static (string Secret, string Hash) NewSecret()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretLength / 2)).ToLowerInvariant();
        return (secret, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
    }

    private static ApiTokenSettings Normalize(ApiTokenSettings? s)
    {
        s ??= new ApiTokenSettings();
        return new ApiTokenSettings
        {
            AllowSync = s.AllowSync,
            AllowAsync = s.AllowAsync,
            MaxUploadMb = s.MaxUploadMb,
            RequestsPerMinute = s.RequestsPerMinute,
            MaxActiveJobs = s.MaxActiveJobs,
            FixedText = Clean(s.FixedText?.Replace("\r\n", "\n")),
            AllowUserOverride = s.AllowUserOverride,
            AllowedNetworks = s.AllowedNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApiToken Clone(ApiToken t) =>
        JsonSerializer.Deserialize<ApiToken>(JsonSerializer.SerializeToUtf8Bytes(t, Json), Json)!;

    private void SaveLocked()
    {
        _dirty = false;
        if (_file is null) return;
        try
        {
            Storage.WriteAtomic(_file, JsonSerializer.SerializeToUtf8Bytes(_tokens.Values.ToList(), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
            _log.LogError(ex, "Could not save the API tokens to {File}", _file);
        }
    }
}

/// <summary>Limite di richieste al minuto per token (finestra fissa di un minuto).</summary>
public sealed class ApiRateLimiter
{
    private sealed class Window { public long Minute; public int Count; }
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public bool TryAcquire(string tokenId, int limit, out int retryAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minute = now / 60;
        var w = _windows.GetOrAdd(tokenId, _ => new Window());
        lock (w)
        {
            if (w.Minute != minute) { w.Minute = minute; w.Count = 0; }
            if (w.Count >= limit)
            {
                retryAfterSeconds = (int)(60 - now % 60);
                return false;
            }
            w.Count++;
        }
        retryAfterSeconds = 0;
        return true;
    }
}

/// <summary>Piccole utilità per i file persistenti.</summary>
public static class Storage
{
    public static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Scrive su un file temporaneo e lo rinomina: un'interruzione non lascia mai il file a metà.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
