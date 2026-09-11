namespace flow_weaver_backend.Models;

// Application-wide operational settings. Exactly one row ever exists, keyed by
// the fixed SingletonId, so callers address "the settings" without carrying a
// lookup key around. Deliberately does NOT inherit BaseModel: it is neither
// soft-deletable nor listable.
public class AppSetting
{
    // The only primary key value this table ever holds. Writers upsert against
    // it; readers fetch it directly.
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    // When true, mutations on workflows / integrations consult
    // IResourcePermissionService.HasAtLeastAsync. Default false keeps the
    // global Admin/Operator/Viewer behaviour.
    public bool PermissionsGranularGatingEnabled { get; set; }

    // "legacy" | "granular" — rollout switch for the capability-based HTTP
    // layer. Lets an admin flip the deployment over (and roll back) without a
    // deploy.
    public string RbacMode { get; set; } = "legacy";

    // Thresholds for the import wizard's fuzzy action-name matcher. When the
    // exact match for an `action_name` fails, the resolver ranks every action
    // under the same integration and auto-applies the top candidate iff (a) its
    // score >= ImportFuzzyMatchThreshold and (b) its lead over the runner-up
    // >= ImportFuzzyMatchGap.
    public double ImportFuzzyMatchThreshold { get; set; } = 0.8;
    public double ImportFuzzyMatchGap { get; set; } = 0.1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
