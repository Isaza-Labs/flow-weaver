namespace flow_weaver_backend.Services.Auth;

// Bound to the "Jwt" section of appsettings. Jwt:Key is the signing secret —
// must be 32+ bytes of high-entropy random in production. In dev the value
// in appsettings.json is a placeholder and should be overridden via the
// `Jwt__Key` environment variable for any non-local deployment.
public class JwtOptions
{
    public const string SectionName = "Jwt";

    // Tokens we explicitly recognise as insecure defaults shipped in
    // appsettings.json and docker-compose.yml. Boot fails if any of these
    // is loaded outside the Development environment.
    public static readonly IReadOnlySet<string> KnownDefaultKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "dev-only-key-replace-in-prod-with-32plus-random-bytes-via-env",
        "ThisIsADevelopmentKeyThatMustBeAtLeast32CharsLong!"
    };

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public bool IsKnownDefaultKey() => !string.IsNullOrEmpty(Key) && KnownDefaultKeys.Contains(Key);

    // Shared between Program.cs and the boot integration tests
    // so the rule lives in exactly one place. Throws when:
    //   • the section was missing entirely (jwt is null),
    //   • the key is empty or shorter than 32 chars,
    //   • the key matches a repo placeholder and the environment is not
    //     "Development".
    // The string compared against environmentName is the same value
    // ASPNETCORE_ENVIRONMENT carries (case-sensitive on Linux).
    public static void ValidateForBoot(JwtOptions? jwt, string environmentName)
    {
        if (jwt is null)
            throw new InvalidOperationException("Jwt section missing from configuration.");
        if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Key must be 32+ chars. Set Jwt__Key env var in production.");
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.Ordinal);
        if (!isDevelopment && jwt.IsKnownDefaultKey())
            throw new InvalidOperationException(
                "Jwt:Key is set to a known development default. Refusing to start outside Development. " +
                "Generate a per-instance key (e.g. `openssl rand -base64 48`) and inject via the Jwt__Key env var.");
    }
}

// Bound to the "Auth" section. Controls password strength and account
// lockout behavior. Keep the password rules in sync with the frontend
// form validator (Sprint 6.2).
public class AuthOptions
{
    public const string SectionName = "Auth";

    public PasswordPolicyOptions PasswordPolicy { get; set; } = new();
    public LockoutOptions Lockout { get; set; } = new();
}

public class PasswordPolicyOptions
{
    public int MinLength { get; set; } = 12;
    public bool RequireUpper { get; set; } = true;
    public bool RequireLower { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireSymbol { get; set; } = true;
}

public class LockoutOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
