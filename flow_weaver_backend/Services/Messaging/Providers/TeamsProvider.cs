using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace flow_weaver_backend.Services.Messaging.Providers;

// Microsoft Teams (Bot Framework) channel. Inbound Activities carry a Bearer JWT
// issued by the Bot Framework; we validate it against the public OpenID metadata
// (issuer + audience=app_id + signature + the serviceurl claim). Outbound replies
// need an AAD client-credentials token (app_id + app secret) posted back to the
// activity's serviceUrl. ExternalThreadId encodes "{serviceUrl}::{conversationId}"
// so the generic OutboundMessage carries everything the reply needs.
//
// The issuer is api.botframework.com for every bot app type (multi-tenant,
// single-tenant, managed identity) — only the OUTBOUND token authority differs,
// and that is what external_config.tenant_id selects.
public sealed partial class TeamsProvider : IMessagingProvider
{
    private const string BotFrameworkIssuer = "https://api.botframework.com";
    private const string OpenIdConfigUrl = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    private const string ThreadSeparator = "::";
    // The Connector→Bot token names the one service endpoint it may be used
    // against, lowercase per the Bot Framework spec.
    private const string ServiceUrlClaim = "serviceurl";
    // Teams user ids are "29:…"; a bot's own id is "28:…". Dropping those is a
    // loop guard for the case where the bot sees an activity it authored.
    private const string BotIdPrefix = "28:";
    // Renew a little before expiry so an in-flight reply never races the clock.
    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TeamsProvider> _logger;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configManager;
    // AAD client-credentials tokens live ~1h; the provider is a singleton, so
    // caching them turns two HTTP hops per reply into one. Keyed by channel +
    // app id + a digest of the secret, so rotating any of them misses the cache
    // instead of reusing a token minted from the old credentials.
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokenCache = new();

    // The DI constructor: the signing keys come from the Bot Framework's public
    // metadata endpoint (cached and refreshed by ConfigurationManager).
    public TeamsProvider(IHttpClientFactory httpFactory, ILogger<TeamsProvider> logger)
        : this(httpFactory, logger, new ConfigurationManager<OpenIdConnectConfiguration>(
            OpenIdConfigUrl, new OpenIdConnectConfigurationRetriever()))
    {
    }

    // Tests substitute the metadata source so the whole verification path —
    // issuer, audience, lifetime, signature and the serviceurl claim — can be
    // exercised without reaching the internet.
    public TeamsProvider(
        IHttpClientFactory httpFactory, ILogger<TeamsProvider> logger,
        IConfigurationManager<OpenIdConnectConfiguration> configManager)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _configManager = configManager;
    }

    public string Provider => MessagingChannel.ProviderTeams;

    public async Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(request.Method))
            return WebhookVerifyResult.Verified();

        var auth = request.Header("Authorization");
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return channel.AllowUnsigned
                ? WebhookVerifyResult.Verified()
                : WebhookVerifyResult.Rejected(401, "missing bearer token");

        var appId = ConfigStr(channel, "app_id");
        if (string.IsNullOrEmpty(appId))
            return WebhookVerifyResult.Rejected(401, "channel has no app_id in external_config");

        var token = auth["Bearer ".Length..].Trim();
        try
        {
            var config = await _configManager.GetConfigurationAsync(ct);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = BotFrameworkIssuer,
                ValidateAudience = true,
                ValidAudience = appId,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
            };
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
            if (!result.IsValid)
                return WebhookVerifyResult.Rejected(401, "invalid bot framework token");

            // The token pins the endpoint it authorizes. Replies carry an AAD
            // bearer to that endpoint, so a token whose serviceurl disagrees
            // with the activity is refused rather than followed — otherwise a
            // token minted for another deployment could redirect our credential.
            return VerifyServiceUrlClaim(result, request.Body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "messaging.teams.verify_failed channel={Channel}", channel.MessagingChannelId);
            return WebhookVerifyResult.Rejected(401, "token validation failed");
        }
    }

    private static WebhookVerifyResult VerifyServiceUrlClaim(TokenValidationResult result, byte[] body)
    {
        var claimed = result.Claims.TryGetValue(ServiceUrlClaim, out var raw) ? raw as string : null;
        if (string.IsNullOrEmpty(claimed))
            return WebhookVerifyResult.Rejected(401, "token has no serviceurl claim");

        string activityUrl;
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(body);
            activityUrl = root.ValueKind == JsonValueKind.Object ? Str(root, "serviceUrl") : string.Empty;
        }
        catch (JsonException) { activityUrl = string.Empty; }

        if (activityUrl.Length == 0)
            return WebhookVerifyResult.Rejected(401, "activity has no serviceUrl");

        return string.Equals(
            claimed.TrimEnd('/'), activityUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? WebhookVerifyResult.Verified()
            : WebhookVerifyResult.Rejected(401, "serviceurl claim does not match the activity");
    }

    public InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request)
    {
        if (request.Body.Length == 0) return null;
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(request.Body); }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (Str(root, "type") != "message") return null;
        var id = Str(root, "id");
        var serviceUrl = Str(root, "serviceUrl");
        if (id.Length == 0 || serviceUrl.Length == 0) return null;

        var conversationId = root.TryGetProperty("conversation", out var conv) ? Str(conv, "id") : string.Empty;
        if (conversationId.Length == 0) return null;

        string userId = string.Empty, display = string.Empty, fromId = string.Empty;
        if (root.TryGetProperty("from", out var from))
        {
            fromId = Str(from, "id");
            // aadObjectId is the stable cross-session identity; fall back to the
            // channel-specific id.
            userId = Str(from, "aadObjectId");
            if (userId.Length == 0) userId = fromId;
            display = Str(from, "name");
        }
        if (userId.Length == 0) return null;
        // Never answer ourselves (or any other bot in the conversation).
        if (fromId.StartsWith(BotIdPrefix, StringComparison.Ordinal)) return null;

        // In a channel or group chat the bot only receives messages that
        // @mention it, and that mention is part of the text as markup. Left in,
        // the agent reads "<at>FlowWeaver</at> list devices" as the prompt.
        var text = CleanText(root, Str(root, "text"));
        // A mention with nothing else (or a bare attachment) carries no prompt.
        if (text.Length == 0) return null;

        var tenantId = root.TryGetProperty("channelData", out var cd) && cd.TryGetProperty("tenant", out var ten)
            ? Str(ten, "id") : string.Empty;

        return new InboundMessage
        {
            ProviderEventId = id,
            ExternalWorkspaceId = tenantId,
            ExternalUserId = userId,
            ExternalThreadId = $"{serviceUrl}{ThreadSeparator}{conversationId}",
            Text = text,
            SenderDisplayName = display.Length > 0 ? display : null,
            EventKind = "message",
        };
    }

    // Turns Teams' mention markup into what the user actually meant: the bot's
    // own mention disappears, everyone else's collapses to their display name.
    private static string CleanText(JsonElement root, string text)
    {
        if (text.Length == 0) return text;

        var recipientId = root.TryGetProperty("recipient", out var rec) ? Str(rec, "id") : string.Empty;
        if (recipientId.Length > 0
            && root.TryGetProperty("entities", out var entities)
            && entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(Str(entity, "type"), "mention", StringComparison.OrdinalIgnoreCase)) continue;
                var markup = Str(entity, "text");
                if (markup.Length == 0) continue;
                var mentionedId = entity.TryGetProperty("mentioned", out var m) ? Str(m, "id") : string.Empty;
                if (string.Equals(mentionedId, recipientId, StringComparison.Ordinal))
                    text = text.Replace(markup, string.Empty, StringComparison.Ordinal);
            }
        }

        // Whatever the entity list missed — another user's mention, or a client
        // that inlined the tag without an entity — is unwrapped to its label
        // rather than reaching the agent as markup.
        text = AtTagRegex().Replace(text, "$1");
        return WebUtility.HtmlDecode(text).Trim();
    }

    public async Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(decryptedBotToken))
            throw new InvalidOperationException("teams channel has no app secret configured");
        var appId = ConfigStr(channel, "app_id");
        if (string.IsNullOrEmpty(appId))
            throw new InvalidOperationException("teams channel has no app_id in external_config");

        var (serviceUrl, conversationId) = SplitThread(message.ExternalThreadId);
        if (serviceUrl is null || conversationId is null)
            throw new InvalidOperationException("teams thread id missing serviceUrl/conversationId");
        // Defense in depth: the AAD bearer token is attached to this request, so
        // never send it anywhere but an https Bot Framework endpoint. (serviceUrl
        // ultimately originates from the inbound activity, which is JWT-verified
        // down to its serviceurl claim, but we don't want a malformed/forged
        // value to exfiltrate the token.)
        if (!serviceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("teams serviceUrl must be https");

        var accessToken = await AcquireAadTokenAsync(channel, appId, decryptedBotToken, ct);

        var payload = JsonSerializer.Serialize(new
        {
            type = "message",
            // Agent replies are markdown; Teams renders them only when told so.
            textFormat = "markdown",
            text = Truncate(message.Text, 28000),
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var http = _httpFactory.CreateClient("messaging");
        var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{conversationId}/activities";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            // A rejected token is worth forgetting: the next attempt then mints
            // a fresh one instead of replaying a revoked credential.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                _tokenCache.TryRemove(CacheKey(channel, appId, decryptedBotToken), out _);
            _logger.LogWarning(
                "messaging.teams.send_failed channel={Channel} status={Status} body={Body}",
                channel.MessagingChannelId, (int)resp.StatusCode, Truncate(body, 300));
            throw new InvalidOperationException($"teams reply failed: {(int)resp.StatusCode}");
        }
    }

    private async Task<string> AcquireAadTokenAsync(
        MessagingChannel channel, string appId, string appSecret, CancellationToken ct)
    {
        var key = CacheKey(channel, appId, appSecret);
        if (_tokenCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Token;

        // Multi-tenant bots authenticate against the shared botframework.com
        // authority; single-tenant and managed-identity bots against their own.
        var tenantId = ConfigStr(channel, "tenant_id");
        var tokenUrl = string.IsNullOrEmpty(tenantId)
            ? "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token"
            : $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = appId,
            ["client_secret"] = appSecret,
            ["scope"] = "https://api.botframework.com/.default",
        });

        var http = _httpFactory.CreateClient("messaging");
        using var resp = await http.PostAsync(tokenUrl, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"teams AAD token failed: {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString()!
            : throw new InvalidOperationException("teams AAD token response missing access_token");

        // Cache only when AAD tells us how long it is good for; a response
        // without expires_in is used once rather than guessed at.
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
        {
            var lifetime = TimeSpan.FromSeconds(seconds) - TokenExpirySkew;
            if (lifetime > TimeSpan.Zero)
                _tokenCache[key] = (token, DateTimeOffset.UtcNow + lifetime);
        }
        return token;
    }

    // The secret is hashed, never stored: the cache key lives in memory next to
    // the token but should not itself be a copy of the credential.
    private static string CacheKey(MessagingChannel channel, string appId, string appSecret)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(appSecret)))[..16];
        return $"{channel.MessagingChannelId}:{appId}:{digest}";
    }

    private static (string? serviceUrl, string? conversationId) SplitThread(string composite)
    {
        var idx = composite.IndexOf(ThreadSeparator, StringComparison.Ordinal);
        return idx < 0 ? (null, null) : (composite[..idx], composite[(idx + ThreadSeparator.Length)..]);
    }

    private static string ConfigStr(MessagingChannel channel, string name) =>
        channel.ExternalConfig.ValueKind == JsonValueKind.Object
        && channel.ExternalConfig.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex("<at\\b[^>]*>(.*?)</at>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AtTagRegex();
}
