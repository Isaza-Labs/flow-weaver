namespace flow_weaver_backend.Services.Security;

// CORS policy registration. Origins come from Cors:AllowedOrigins in
// appsettings — an array of fully-qualified URLs (scheme + host + port).
//
// Rules:
//   - Allowlist only. Never AllowAnyOrigin — credentials are required.
//   - AllowCredentials is needed because the frontend will send the
//     Authorization: Bearer header (Sprint 1.3) and cookies for SSE
//     sessions (future).
//   - If the config is missing or empty, the policy allows nothing —
//     the frontend will get CORS errors until config is set. This is
//     intentional: we refuse to run open on accident.
public static class CorsConfiguration
{
    public const string PolicyName = "Frontend";

    public static IServiceCollection AddFlowWeaverCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();

        services.AddCors(options =>
        {
            options.AddPolicy(PolicyName, builder =>
            {
                if (allowedOrigins.Length == 0)
                {
                    // Refuse to open — any cross-origin request will be blocked.
                    return;
                }

                builder
                    .WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    // Required so the frontend can read the Retry-After
                    // header emitted by the rate limiter on 429.
                    .WithExposedHeaders("Retry-After");
            });
        });

        return services;
    }
}
