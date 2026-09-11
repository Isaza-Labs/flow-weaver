using flow_weaver_backend.Services.Auth;

namespace flow_weaver_backend.Tests;

// S12.3: integration-style coverage of the boot guard called from
// Program.cs. The validator runs before the host is built, so we don't
// need WebApplicationFactory to exercise it — invoking the static method
// with the same inputs Program.cs feeds it covers every branch.
public class JwtBootValidationTests
{
    private const string ValidLongKey = "u4Tn7N1mTxJ9mP4mO0Vw6IFxQ8Rl1bQrW3v0KeJ4Rk2k1Hq9d8";
    private const string AppsettingsDefault = "dev-only-key-replace-in-prod-with-32plus-random-bytes-via-env";
    private const string ComposeDefault = "ThisIsADevelopmentKeyThatMustBeAtLeast32CharsLong!";

    [Fact]
    public void Throws_when_jwt_section_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.ValidateForBoot(null, "Production"));
        Assert.Contains("Jwt section missing", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooShort")]
    public void Throws_when_key_blank_or_too_short(string key)
    {
        var opts = new JwtOptions { Key = key };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.ValidateForBoot(opts, "Production"));
        Assert.Contains("32+ chars", ex.Message);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    public void Throws_when_default_key_outside_development(string env)
    {
        var opts = new JwtOptions { Key = AppsettingsDefault };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.ValidateForBoot(opts, env));
        Assert.Contains("known development default", ex.Message);
    }

    [Fact]
    public void Throws_when_compose_default_key_outside_development()
    {
        var opts = new JwtOptions { Key = ComposeDefault };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.ValidateForBoot(opts, "Production"));
        Assert.Contains("known development default", ex.Message);
    }

    [Theory]
    [InlineData(AppsettingsDefault)]
    [InlineData(ComposeDefault)]
    public void Allows_default_key_only_in_Development(string defaultKey)
    {
        var opts = new JwtOptions { Key = defaultKey };
        // Should not throw.
        JwtOptions.ValidateForBoot(opts, "Development");
    }

    [Fact]
    public void Allows_random_key_in_any_environment()
    {
        var opts = new JwtOptions { Key = ValidLongKey };
        JwtOptions.ValidateForBoot(opts, "Production");
        JwtOptions.ValidateForBoot(opts, "Staging");
        JwtOptions.ValidateForBoot(opts, "Development");
    }

    [Fact]
    public void Environment_match_is_case_sensitive()
    {
        // Linux env vars treat ASPNETCORE_ENVIRONMENT as case-sensitive.
        // "development" (lowercase) should NOT pass when the key is a
        // known default — protects against typos that would silently
        // accept the placeholder.
        var opts = new JwtOptions { Key = AppsettingsDefault };
        Assert.Throws<InvalidOperationException>(() =>
            JwtOptions.ValidateForBoot(opts, "development"));
    }
}
