using flow_weaver_backend.Services.Auth;

namespace flow_weaver_backend.Tests;

// Verifies that the JwtOptions guard recognises the development-default
// keys shipped in the repo. Program.cs uses this to refuse boot outside
// the Development environment.
public class JwtOptionsDefaultKeyTests
{
    [Fact]
    public void IsKnownDefaultKey_returns_true_for_appsettings_default()
    {
        var opts = new JwtOptions { Key = "dev-only-key-replace-in-prod-with-32plus-random-bytes-via-env" };
        Assert.True(opts.IsKnownDefaultKey());
    }

    [Fact]
    public void IsKnownDefaultKey_returns_true_for_compose_default()
    {
        var opts = new JwtOptions { Key = "ThisIsADevelopmentKeyThatMustBeAtLeast32CharsLong!" };
        Assert.True(opts.IsKnownDefaultKey());
    }

    [Fact]
    public void IsKnownDefaultKey_returns_false_for_random_key()
    {
        var opts = new JwtOptions { Key = "u4Tn7N1mTxJ9mP4mO0Vw6IFxQ8Rl1bQrW3v0KeJ4Rk2k1Hq9d8" };
        Assert.False(opts.IsKnownDefaultKey());
    }

    [Fact]
    public void IsKnownDefaultKey_returns_false_for_empty()
    {
        var opts = new JwtOptions { Key = string.Empty };
        Assert.False(opts.IsKnownDefaultKey());
    }
}
