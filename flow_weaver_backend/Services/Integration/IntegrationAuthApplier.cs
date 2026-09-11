using System.Net.Http.Headers;
using flow_weaver_backend.Dtos;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

// Async layer over IntegrationAuthBuilder: applies the integration's custom
// headers + static auth exactly like the sync builder, and, when the auth
// method is `oauth2_client_credentials`, obtains the access token from
// IntegrationOAuthTokenService and sets it as a Bearer header.
//
// Every integration call site that can await should use this instead of the
// raw builder — the builder stays for the one place that genuinely cannot
// (none today) and as the shared header mechanics underneath.
public interface IIntegrationAuthApplier
{
    // Throws InvalidOperationException when an OAuth token grant fails, so
    // callers surface "credentials broken" rather than sending an anonymous
    // request that hits a confusing upstream 401.
    Task ApplyAsync(HttpRequestMessage request, IntegrationModel integration, CancellationToken ct = default);
}

public sealed class IntegrationAuthApplier : IIntegrationAuthApplier
{
    private readonly IntegrationAuthBuilder _builder;
    private readonly IIntegrationOAuthTokenService _tokens;

    public IntegrationAuthApplier(IntegrationAuthBuilder builder, IIntegrationOAuthTokenService tokens)
    {
        _builder = builder;
        _tokens = tokens;
    }

    public async Task ApplyAsync(HttpRequestMessage request, IntegrationModel integration, CancellationToken ct = default)
    {
        // Custom headers + every static method; a no-op for the OAuth method.
        _builder.Apply(request, integration);

        var auth = IntegrationAuthBuilder.TryParse(integration.AuthConfig);
        if (auth is null
            || !string.Equals(auth.Method, IntegrationAuthConfig.OAuth2ClientCredentials, StringComparison.OrdinalIgnoreCase))
            return;

        var accessToken = await _tokens.GetAccessTokenAsync(integration, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
