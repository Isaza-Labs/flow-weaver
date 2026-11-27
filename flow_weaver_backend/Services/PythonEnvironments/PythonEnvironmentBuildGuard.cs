using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.PythonEnvironments;

// The builder's "is what I claimed still what is wanted?" check, kept pure so
// it is unit-testable without uv or a database.
//
// A build is for one definition: the row's own Requirements and PythonVersion
// AND the pip specs the Packages allowlist contributes to it (together, the
// EFFECTIVE requirements). An admin edit of either while the build runs puts
// the row back to `pending`; the build must then be dropped rather than
// swapped in and marked ready with packages nobody asked for any more.
public static class PythonEnvironmentBuildGuard
{
    // What the builder read when it claimed the row.
    public sealed record Claim(
        Guid PythonEnvironmentId,
        string Name,
        string Requirements,
        string? PythonVersion,
        string EffectiveRequirements);

    public static Claim Capture(PythonEnvironment env, string effectiveRequirements) => new(
        env.PythonEnvironmentId, env.Name, env.Requirements, env.PythonVersion, effectiveRequirements);

    // True when `current` (re-read just before the swap) and its effective
    // requirements still match the claim and the row is still ours to finish.
    public static bool IsStillCurrent(Claim claim, PythonEnvironment? current, string? currentEffectiveRequirements)
        => current is not null
            && current.PythonEnvironmentId == claim.PythonEnvironmentId
            && current.IsActive
            && current.Status == PythonEnvironment.StatusBuilding
            && string.Equals(current.Name, claim.Name, StringComparison.Ordinal)
            && string.Equals(current.Requirements, claim.Requirements, StringComparison.Ordinal)
            && string.Equals(current.PythonVersion, claim.PythonVersion, StringComparison.Ordinal)
            && string.Equals(currentEffectiveRequirements, claim.EffectiveRequirements, StringComparison.Ordinal);
}
