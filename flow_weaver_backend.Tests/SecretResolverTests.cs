using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// SecretResolver turns `${secret:<source>:<id|name>:<field>}` markers into
// plaintext right before the REST executor hits the wire. These tests pin the
// grammar (what resolves, what stays literal) and the per-source field maps,
// because a silent miss here ships a request with a literal marker instead of
// a credential — and an over-eager match leaks one into the wrong field.
public class SecretResolverTests
{
    // Mints a recognisable token so tests can assert the background path was
    // taken without standing up real JWT signing.
    private sealed class FakeJwt : IJwtTokenService
    {
        public List<IEnumerable<string>?> CeilingsSeen { get; } = new();

        public string CreateAccessToken(User user, out DateTime expiresAt)
        {
            expiresAt = DateTime.UtcNow.AddMinutes(5);
            return "minted-user";
        }

        public string CreateAccessToken(
            Guid userId, string username,
            IEnumerable<string> roles, out DateTime expiresAt)
            => CreateAccessToken(userId, username, roles, null, out expiresAt);

        public string CreateAccessToken(
            Guid userId, string username,
            IEnumerable<string> roles, IEnumerable<string>? capabilityCeiling, out DateTime expiresAt)
        {
            expiresAt = DateTime.UtcNow.AddMinutes(5);
            CeilingsSeen.Add(capabilityCeiling);
            return $"minted:{username}:{string.Join('+', roles)}";
        }
    }

    // Anonymous identity — exercises the "nothing bound, leave it literal" path.
    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
        public string? Username => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string>? CapabilityCeiling => null;
    }

    private static SecretResolver Build(
        AppDbContext db,
        ICurrentUser? caller = null,
        IHttpContextAccessor? http = null,
        IJwtTokenService? jwt = null)
        => new(
            new SecretRepository(db),
            new CredentialRepository(db),
            new AiProviderRepository(db),
            new IntegrationRepository(db),
            caller ?? new FakeUser(),
            new FakeCrypto(),
            http ?? new HttpContextAccessor(),
            jwt ?? new FakeJwt(),
            new FakeTrace(), NullLogger<SecretResolver>.Instance);

    private static IHttpContextAccessor HttpWithAuth(string? headerValue)
    {
        var ctx = new DefaultHttpContext();
        if (headerValue is not null) ctx.Request.Headers.Authorization = headerValue;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    // ─── secret source ──────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_Secret_ByName_ReturnsDecryptedValue()
    {
        using var db = TestDb.NewContext();
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            EncryptedValue = Encoding.UTF8.GetBytes("s3cr3t"),
        });
        await db.SaveChangesAsync();

        var value = await Build(db).ResolveAsync("secret", "api-token", "value", default);

        Assert.Equal("s3cr3t", value);
    }

    [Fact]
    public async Task Resolve_Secret_ById_ReturnsDecryptedValue()
    {
        using var db = TestDb.NewContext();
        var id = Guid.NewGuid();
        db.Secrets.Add(new Secret
        {
            SecretId = id,
            Name = "whatever",
            EncryptedValue = Encoding.UTF8.GetBytes("by-id"),
        });
        await db.SaveChangesAsync();

        var value = await Build(db).ResolveAsync("secret", id.ToString(), "value", default);

        Assert.Equal("by-id", value);
    }

    // Only `value` is a legal field on a secret; anything else must miss rather
    // than fall through to some other column.
    [Fact]
    public async Task Resolve_Secret_UnknownField_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            EncryptedValue = Encoding.UTF8.GetBytes("s3cr3t"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await Build(db).ResolveAsync("secret", "api-token", "name", default));
    }

    // Soft-deleted rows are invisible: revoking a secret must break the marker.
    [Fact]
    public async Task Resolve_Secret_Inactive_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(),
            Name = "api-token",
            IsActive = false,
            EncryptedValue = Encoding.UTF8.GetBytes("revoked"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await Build(db).ResolveAsync("secret", "api-token", "value", default));
    }

    // ─── credential source ──────────────────────────────────────────────

    [Theory]
    [InlineData("username", "netadmin")]
    [InlineData("password", "hunter2")]
    [InlineData("private_key", "-----KEY-----")]
    [InlineData("privatekey", "-----KEY-----")]
    [InlineData("PASSWORD", "hunter2")]
    public async Task Resolve_Credential_MapsEachField(string field, string expected)
    {
        using var db = TestDb.NewContext();
        db.Credentials.Add(new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "core-switch",
            Username = "netadmin",
            EncryptedPassword = Encoding.UTF8.GetBytes("hunter2"),
            EncryptedPrivateKey = Encoding.UTF8.GetBytes("-----KEY-----"),
        });
        await db.SaveChangesAsync();

        var value = await Build(db).ResolveAsync("credential", "core-switch", field, default);

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task Resolve_Credential_UnknownField_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        db.Credentials.Add(new Credential
        {
            CredentialId = Guid.NewGuid(),
            Name = "core-switch",
            Username = "netadmin",
        });
        await db.SaveChangesAsync();

        Assert.Null(await Build(db).ResolveAsync("credential", "core-switch", "passphrase", default));
    }

    [Fact]
    public async Task Resolve_Credential_Missing_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        Assert.Null(await Build(db).ResolveAsync("credential", "nope", "password", default));
    }

    // ─── ai_provider source ─────────────────────────────────────────────

    [Theory]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("API_KEY")]
    public async Task Resolve_AiProvider_ApiKeyAliases(string field)
    {
        using var db = TestDb.NewContext();
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "openai-prod",
            EncryptedApiKey = Encoding.UTF8.GetBytes("sk-test"),
        });
        await db.SaveChangesAsync();

        Assert.Equal("sk-test", await Build(db).ResolveAsync("ai_provider", "openai-prod", field, default));
    }

    [Fact]
    public async Task Resolve_AiProvider_OtherField_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "openai-prod",
            EncryptedApiKey = Encoding.UTF8.GetBytes("sk-test"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await Build(db).ResolveAsync("ai_provider", "openai-prod", "base_url", default));
    }

    // ─── integration source (dotted path into AuthConfig) ───────────────

    private static async Task<AppDbContext> DbWithIntegration(string authConfigJson)
    {
        var db = TestDb.NewContext();
        db.Integrations.Add(new Integration
        {
            IntegrationId = Guid.NewGuid(),
            Name = "netbox",
            BaseURL = "https://netbox.example.com",
            AuthConfig = TestJson.Element(authConfigJson),
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Resolve_Integration_TopLevelStringField()
    {
        using var db = await DbWithIntegration("""{"token":"abc123"}""");

        Assert.Equal("abc123", await Build(db).ResolveAsync("integration", "netbox", "token", default));
    }

    [Fact]
    public async Task Resolve_Integration_DottedPath()
    {
        using var db = await DbWithIntegration("""{"basic":{"password":"p@ss"}}""");

        Assert.Equal("p@ss", await Build(db).ResolveAsync("integration", "netbox", "basic.password", default));
    }

    // Numbers and booleans come back as raw JSON text so they can be
    // interpolated into a template without a type-specific accessor.
    [Theory]
    [InlineData("""{"port":8443}""", "port", "8443")]
    [InlineData("""{"verify":true}""", "verify", "true")]
    [InlineData("""{"verify":false}""", "verify", "false")]
    public async Task Resolve_Integration_ScalarKinds(string json, string field, string expected)
    {
        using var db = await DbWithIntegration(json);

        Assert.Equal(expected, await Build(db).ResolveAsync("integration", "netbox", field, default));
    }

    // A path that lands on an object/array/null is not a usable secret value.
    [Theory]
    [InlineData("""{"basic":{"password":"p"}}""", "basic")]
    [InlineData("""{"scopes":["a","b"]}""", "scopes")]
    [InlineData("""{"token":null}""", "token")]
    [InlineData("""{"basic":{"password":"p"}}""", "basic.missing")]
    [InlineData("""{"token":"abc"}""", "token.deeper")]
    public async Task Resolve_Integration_NonScalarOrMissingPath_ReturnsNull(string json, string field)
    {
        using var db = await DbWithIntegration(json);

        Assert.Null(await Build(db).ResolveAsync("integration", "netbox", field, default));
    }

    [Fact]
    public async Task Resolve_Integration_NonObjectAuthConfig_ReturnsNull()
    {
        using var db = await DbWithIntegration("\"not-an-object\"");

        Assert.Null(await Build(db).ResolveAsync("integration", "netbox", "token", default));
    }

    // ─── session source ─────────────────────────────────────────────────

    // Web path: reuse the caller's own bearer verbatim so the self-call runs
    // with exactly the chatting user's permissions.
    [Fact]
    public async Task Resolve_Session_BorrowsRequestBearer()
    {
        using var db = TestDb.NewContext();
        var jwt = new FakeJwt();
        var resolver = Build(db, http: HttpWithAuth("Bearer caller-token"), jwt: jwt);

        Assert.Equal("caller-token", await resolver.ResolveAsync("session", "current", "jwt", default));
        Assert.Empty(jwt.CeilingsSeen); // never minted a new one
    }

    [Fact]
    public async Task Resolve_Session_BearerPrefixIsCaseInsensitive()
    {
        using var db = TestDb.NewContext();
        var resolver = Build(db, http: HttpWithAuth("bearer caller-token"));

        Assert.Equal("caller-token", await resolver.ResolveAsync("session", "current", "jwt", default));
    }

    // A non-bearer scheme is not something we can forward.
    [Fact]
    public async Task Resolve_Session_NonBearerScheme_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        var resolver = Build(db, http: HttpWithAuth("Basic dXNlcjpwYXNz"));

        Assert.Null(await resolver.ResolveAsync("session", "current", "jwt", default));
    }

    // Background path (messaging / scheduled runs): no request to borrow from,
    // so mint a token for the identity bound on ICurrentUser.
    [Fact]
    public async Task Resolve_Session_NoRequest_MintsFromBoundIdentity()
    {
        using var db = TestDb.NewContext();
        var jwt = new FakeJwt();
        var caller = new FakeUser { Username = "botuser", Roles = new[] { "operator" } };
        var resolver = Build(db, caller: caller, http: new HttpContextAccessor(), jwt: jwt);

        var value = await resolver.ResolveAsync("session", "current", "jwt", default);

        Assert.Equal("minted:botuser:operator", value);
    }

    // The channel's capability ceiling must survive into the minted token,
    // otherwise the self-call would widen past what the transport allows.
    [Fact]
    public async Task Resolve_Session_MintedTokenCarriesCapabilityCeiling()
    {
        using var db = TestDb.NewContext();
        var jwt = new FakeJwt();
        var caller = new FakeUser { CapabilityCeiling = new[] { "workflow.read" } };
        var resolver = Build(db, caller: caller, http: new HttpContextAccessor(), jwt: jwt);

        await resolver.ResolveAsync("session", "current", "jwt", default);

        var ceiling = Assert.Single(jwt.CeilingsSeen);
        Assert.Equal(new[] { "workflow.read" }, ceiling);
    }

    [Fact]
    public async Task Resolve_Session_AnonymousAndNoRequest_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        var resolver = Build(db, caller: new AnonymousUser(), http: new HttpContextAccessor());

        Assert.Null(await resolver.ResolveAsync("session", "current", "jwt", default));
    }

    // Only `session:current:jwt` is legal — no other selector or field.
    [Theory]
    [InlineData("other", "jwt")]
    [InlineData("current", "token")]
    public async Task Resolve_Session_OnlyCurrentJwtIsAccepted(string idOrName, string field)
    {
        using var db = TestDb.NewContext();
        var resolver = Build(db, http: HttpWithAuth("Bearer caller-token"));

        Assert.Null(await resolver.ResolveAsync("session", idOrName, field, default));
    }

    [Fact]
    public async Task Resolve_UnknownSource_ReturnsNull()
    {
        using var db = TestDb.NewContext();
        Assert.Null(await Build(db).ResolveAsync("vault", "thing", "value", default));
    }

    // ─── SubstituteAsync ────────────────────────────────────────────────

    private static async Task<AppDbContext> DbWithSecrets(params (string Name, string Value)[] rows)
    {
        var db = TestDb.NewContext();
        foreach (var (name, value) in rows)
        {
            db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(),
                Name = name,
                EncryptedValue = Encoding.UTF8.GetBytes(value),
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Substitute_ReplacesSingleMarkerInPlace()
    {
        using var db = await DbWithSecrets(("api-token", "s3cr3t"));

        var result = await Build(db).SubstituteAsync(
            "Authorization: Bearer ${secret:secret:api-token:value}", default);

        Assert.Equal("Authorization: Bearer s3cr3t", result);
    }

    [Fact]
    public async Task Substitute_ReplacesMultipleMarkersPreservingSurroundingText()
    {
        using var db = await DbWithSecrets(("user", "alice"), ("pass", "wonderland"));

        var result = await Build(db).SubstituteAsync(
            "u=${secret:secret:user:value};p=${secret:secret:pass:value};end", default);

        Assert.Equal("u=alice;p=wonderland;end", result);
    }

    // Unresolved markers stay literal on purpose: an empty string would ship a
    // broken request silently, the marker shows up in logs and responses.
    [Fact]
    public async Task Substitute_LeavesUnresolvedMarkerLiteral()
    {
        using var db = await DbWithSecrets(("known", "yes"));

        var result = await Build(db).SubstituteAsync(
            "a=${secret:secret:known:value};b=${secret:secret:missing:value}", default);

        Assert.Equal("a=yes;b=${secret:secret:missing:value}", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no markers here")]
    [InlineData("${other:secret:x:value}")]
    public async Task Substitute_WithoutMarkers_ReturnsInputUnchanged(string template)
    {
        using var db = TestDb.NewContext();

        Assert.Equal(template, await Build(db).SubstituteAsync(template, default));
    }

    // Malformed markers must not be rewritten — the grammar requires all three
    // segments, and square-bracket syntax is deliberately unsupported.
    [Theory]
    [InlineData("${secret:secret:api-token}")]
    [InlineData("${secret:api-token:value")]
    [InlineData("${secret[secret][api-token][value]}")]
    public async Task Substitute_MalformedMarker_StaysLiteral(string template)
    {
        using var db = await DbWithSecrets(("api-token", "s3cr3t"));

        Assert.Equal(template, await Build(db).SubstituteAsync(template, default));
    }

    // The source and field segments are matched case-insensitively by the regex.
    [Fact]
    public async Task Substitute_SourceAndFieldAreCaseInsensitive()
    {
        using var db = await DbWithSecrets(("api-token", "s3cr3t"));

        Assert.Equal("s3cr3t", await Build(db).SubstituteAsync("${secret:SECRET:api-token:VALUE}", default));
    }

    // ...but the literal `${secret:` prefix is not: SubstituteCoreAsync short-
    // circuits on an Ordinal Contains before the regex ever runs, so an
    // upper-cased prefix is left untouched. Pinned because the asymmetry is
    // surprising — the fast path, not the grammar, is what rejects it.
    [Fact]
    public async Task Substitute_UpperCasedPrefix_IsNotSubstituted()
    {
        using var db = await DbWithSecrets(("api-token", "s3cr3t"));
        const string template = "${SECRET:secret:api-token:value}";

        Assert.Equal(template, await Build(db).SubstituteAsync(template, default));
    }
}
