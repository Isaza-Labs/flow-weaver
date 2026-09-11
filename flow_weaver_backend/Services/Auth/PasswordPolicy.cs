using System.Reflection;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Auth;

public class PasswordPolicy : IPasswordPolicy
{
    private readonly PasswordPolicyOptions _options;
    private readonly HashSet<string> _commonPasswords;
    private readonly ILogger<PasswordPolicy> _logger;

    public PasswordPolicy(IOptions<AuthOptions> options, ILogger<PasswordPolicy> logger)
    {
        _options = options.Value.PasswordPolicy;
        _commonPasswords = LoadCommonPasswords();
        _logger = logger;
    }

    public PasswordValidationResult Validate(string password, string username = "", string email = "")
    {
        var violated = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            return Fail("missing", "password is required", username);
        }

        if (password.Length < _options.MinLength)
            violated.Add("min_length");

        if (_options.RequireUpper && !password.Any(char.IsUpper))
            violated.Add("require_upper");

        if (_options.RequireLower && !password.Any(char.IsLower))
            violated.Add("require_lower");

        if (_options.RequireDigit && !password.Any(char.IsDigit))
            violated.Add("require_digit");

        if (_options.RequireSymbol && !password.Any(IsSymbol))
            violated.Add("require_symbol");

        if (!string.IsNullOrEmpty(username)
            && password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            violated.Add("contains_username");
        }

        if (!string.IsNullOrEmpty(email))
        {
            var localPart = email.Split('@')[0];
            if (!string.IsNullOrEmpty(localPart)
                && password.Contains(localPart, StringComparison.OrdinalIgnoreCase))
            {
                violated.Add("contains_email");
            }
        }

        if (_commonPasswords.Contains(password.ToLowerInvariant()))
            violated.Add("common_password");

        if (violated.Count == 0)
            return PasswordValidationResult.Ok();

        // Surface only the first violation to the caller — keeps the error
        // message UX unchanged — but log every rule hit so operators see
        // the full picture of weak-password patterns being attempted.
        var first = violated[0];
        var message = first switch
        {
            "min_length" => $"password must be at least {_options.MinLength} characters long",
            "require_upper" => "password must contain at least one uppercase letter",
            "require_lower" => "password must contain at least one lowercase letter",
            "require_digit" => "password must contain at least one digit",
            "require_symbol" => "password must contain at least one symbol",
            "contains_username" => "password must not contain your username",
            "contains_email" => "password must not contain your email",
            "common_password" => "password is too common — pick something less predictable",
            _ => "password does not meet policy",
        };

        return Fail(string.Join(",", violated), message, username);
    }

    private PasswordValidationResult Fail(string rules, string message, string username)
    {
        _logger.LogWarning(
            "auth.password.policy_violation rules={Rules} username={Username}",
            rules, string.IsNullOrEmpty(username) ? "(none)" : username);
        return PasswordValidationResult.Fail(message);
    }

    // Anything that is not a letter or digit counts as a symbol. Matches the
    // same definition the frontend validator will use.
    private static bool IsSymbol(char c) => !char.IsLetterOrDigit(c);

    private static HashSet<string> LoadCommonPasswords()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("common-passwords.txt", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
