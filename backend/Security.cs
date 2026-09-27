using System.Net;
using System.Security.Cryptography;
using System.Text;

public sealed class SecurityOptions
{
    public string[] AllowedOrigins { get; init; } = Array.Empty<string>();
    public string[] TrustedProxies { get; init; } = Array.Empty<string>();
    public string[] TrustedNetworks { get; init; } = Array.Empty<string>();
    public string[] BlockedIps { get; init; } = Array.Empty<string>();
    public int RequestsPerMinute { get; init; } = 60;
    public int EnquiryCooldownSeconds { get; init; } = 15;
    public int MaxRequestBodyBytes { get; init; } = 8 * 1024;
    public int AdminMaxFailedAttempts { get; init; } = 5;
    public int MaxProbeStrikes { get; init; } = 3;
    public int BanMinutes { get; init; } = 15;
    public int MinimumAdminKeyLength { get; init; } = 24;
}

/// <summary>
/// In-memory, per-client firewall state: request rate, enquiry cooldown, failed admin
/// logins and scanner strikes. Entries are pruned so a flood of spoofed or rotating
/// addresses cannot grow memory without bound.
/// </summary>
public sealed class ClientGuard
{
    private const int MaxTrackedClients = 50_000;
    private static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(30);

    private readonly object gate = new();
    private readonly Dictionary<string, ClientState> clients = new();
    private readonly SecurityOptions options;
    private DateTimeOffset nextPrune = DateTimeOffset.MinValue;

    public ClientGuard(SecurityOptions options) => this.options = options;

    public bool IsBanned(string client, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var state = Get(client, now);
            retryAfter = state.BannedUntil - now;
            return retryAfter > TimeSpan.Zero;
        }
    }

    public bool TryConsumeRequest(string client, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var state = Get(client, now);
            if (now - state.WindowStart >= TimeSpan.FromMinutes(1))
            {
                state.WindowStart = now;
                state.RequestCount = 0;
            }

            state.RequestCount++;
            retryAfter = state.WindowStart.AddMinutes(1) - now;
            return state.RequestCount <= options.RequestsPerMinute;
        }
    }

    public bool TryAcceptEnquiry(string client)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var state = Get(client, now);
            if (now - state.LastEnquiry < TimeSpan.FromSeconds(options.EnquiryCooldownSeconds))
            {
                return false;
            }

            state.LastEnquiry = now;
            return true;
        }
    }

    /// <returns>True when this failure triggered a ban.</returns>
    public bool RecordAdminFailure(string client)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var state = Get(client, now);
            if (++state.FailedAdminAttempts < options.AdminMaxFailedAttempts)
            {
                return false;
            }

            state.FailedAdminAttempts = 0;
            state.BannedUntil = now.AddMinutes(options.BanMinutes);
            return true;
        }
    }

    public void RecordAdminSuccess(string client)
    {
        lock (gate)
        {
            Get(client, DateTimeOffset.UtcNow).FailedAdminAttempts = 0;
        }
    }

    /// <returns>True when this strike triggered a ban.</returns>
    public bool RecordProbe(string client)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var state = Get(client, now);
            if (++state.ProbeStrikes < options.MaxProbeStrikes)
            {
                return false;
            }

            state.ProbeStrikes = 0;
            state.BannedUntil = now.AddMinutes(options.BanMinutes);
            return true;
        }
    }

    private ClientState Get(string client, DateTimeOffset now)
    {
        if (now >= nextPrune || clients.Count >= MaxTrackedClients)
        {
            Prune(now);
        }

        if (!clients.TryGetValue(client, out var state))
        {
            state = new ClientState { WindowStart = now, LastEnquiry = DateTimeOffset.MinValue };
            clients[client] = state;
        }

        state.LastSeen = now;
        return state;
    }

    private void Prune(DateTimeOffset now)
    {
        nextPrune = now.AddMinutes(1);
        foreach (var (key, state) in clients.ToArray())
        {
            if (now - state.LastSeen > IdleExpiry && state.BannedUntil < now)
            {
                clients.Remove(key);
            }
        }

        // Still full after pruning idle entries: drop the oldest unbanned half rather than grow.
        if (clients.Count >= MaxTrackedClients)
        {
            foreach (var key in clients.Where(pair => pair.Value.BannedUntil < now)
                         .OrderBy(pair => pair.Value.LastSeen)
                         .Take(clients.Count / 2)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                clients.Remove(key);
            }
        }
    }

    private sealed class ClientState
    {
        public DateTimeOffset WindowStart;
        public int RequestCount;
        public DateTimeOffset LastEnquiry;
        public int FailedAdminAttempts;
        public int ProbeStrikes;
        public DateTimeOffset BannedUntil;
        public DateTimeOffset LastSeen;
    }
}

public static class SecurityPipeline
{
    private static readonly string[] AllowedMethods = { "GET", "HEAD", "POST", "OPTIONS" };
    private static readonly string[] ProbeMarkers =
    {
        "..", "%2e", ".php", ".env", ".git", "wp-", "admin.", "phpmyadmin", "cgi-bin", "/etc/", "<script", "%3c",
    };

    public static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Headers that stop browsers sniffing, framing or caching API responses.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cache-Control"] = "no-store";
            await next();
        });

    /// <summary>
    /// Application firewall: blocklist, temporary bans, method allowlist, scanner detection,
    /// body size and content-type enforcement, and per-client rate limiting.
    /// </summary>
    public static IApplicationBuilder UseApplicationFirewall(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<SecurityOptions>();
        var guard = app.ApplicationServices.GetRequiredService<ClientGuard>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Firewall");
        var blocked = new HashSet<string>(options.BlockedIps.Select(ip => IPAddress.Parse(ip).ToString()));

        return app.Use(async (context, next) =>
        {
            var client = ClientKey(context);
            var request = context.Request;

            if (blocked.Contains(client))
            {
                logger.LogWarning("Firewall: blocked address {Client} requested {Path}", client, request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (guard.IsBanned(client, out var banRemaining))
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(banRemaining.TotalSeconds)).ToString();
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!AllowedMethods.Contains(request.Method, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var target = (request.Path.Value + request.QueryString.Value).ToLowerInvariant();
            if (!request.Path.StartsWithSegments("/api") || ProbeMarkers.Any(target.Contains))
            {
                if (guard.RecordProbe(client))
                {
                    logger.LogWarning("Firewall: banned {Client} for scanning (last path {Path})", client, request.Path);
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (request.ContentLength > options.MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (HttpMethods.IsPost(request.Method)
                && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            if (!guard.TryConsumeRequest(client, out var windowRemaining))
            {
                logger.LogWarning("Firewall: rate limit exceeded by {Client}", client);
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(windowRemaining.TotalSeconds)).ToString();
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            await next();
        });
    }

    /// <summary>Compares keys in constant time so response timing does not leak how much matched.</summary>
    public static bool KeysMatch(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    public static bool ContainsControlCharacters(string value, bool allowLineBreaks) =>
        value.Any(character => char.IsControl(character)
            && !(allowLineBreaks && character is '\n' or '\r' or '\t'));
}
