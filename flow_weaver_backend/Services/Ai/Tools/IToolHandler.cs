using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Tools;

public interface IToolHandler
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct);
}

/// <summary>
/// Implemented by handlers whose danger depends on the ARGUMENTS, not just the
/// tool name.
/// </summary>
/// <remarks>
/// PermissionClassifier's matrix is keyed by tool name, so a tool is one tier
/// for every call. That is wrong for the handful of tools where one argument
/// changes the blast radius entirely — <c>create_snippet</c> is ordinary until
/// <c>network_enabled=true</c>, which lifts the python sandbox's network
/// isolation.
///
/// The dispatcher takes the STRICTER of the static tier and whatever this
/// returns, so an implementation can only ever tighten. Returning null means
/// "no escalation, use the static tier".
/// </remarks>
public interface IArgumentSensitiveTier
{
    /// <summary>
    /// A tier constant from <c>PermissionClassifier</c> when these specific
    /// arguments warrant a stricter gate, or null to keep the static tier.
    /// Must not throw — malformed args should return the strict tier, not
    /// bubble up.
    /// </summary>
    string? EscalatedTier(JsonElement args);
}
