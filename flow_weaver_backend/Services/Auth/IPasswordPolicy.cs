namespace flow_weaver_backend.Services.Auth;

// Validates a candidate password against the configured rules + a blacklist
// of well-known leaked passwords. The rules are configurable; the blacklist
// is hardcoded via embedded resource.
public interface IPasswordPolicy
{
    // Returns (IsValid, ErrorMessage). ErrorMessage is user-facing in Spanish
    // when invalid, empty when valid. We surface one reason at a time to
    // avoid overwhelming the user with a list of 5 complaints.
    PasswordValidationResult Validate(string password, string username = "", string email = "");
}

public record PasswordValidationResult(bool IsValid, string Error)
{
    public static PasswordValidationResult Ok() => new(true, string.Empty);
    public static PasswordValidationResult Fail(string reason) => new(false, reason);
}
