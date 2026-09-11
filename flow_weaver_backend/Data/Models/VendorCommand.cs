namespace flow_weaver_backend.Models;

// Catalog of CLI commands the SSH validator considers
// "known-good" for a given Netmiko device_type. Used at workflow
// create/update time and from the AI agent's validate_ssh_commands tool
// so a command that doesn't match any catalog entry surfaces as a
// warning before the runner is ever spawned.
//
// One row = one command (Kind="exact") OR one regex pattern (Kind="pattern").
// The validator first tries an exact-match lookup (case-insensitive,
// whitespace-collapsed) and falls back to walking the patterns. Anything
// not matched becomes a warning with up-to-3 nearest-neighbour
// suggestions from the same device_type's exact entries.
//
// The default catalog is shipped via DefaultVendorCommandsSeedService at
// first boot; rows the admin adds afterwards have Source="user" and
// survive re-seed.
public class VendorCommand : BaseModel
{
    public Guid VendorCommandId { get; set; }

    // Netmiko device_type (cisco_ios, juniper_junos, nokia_srl, …). The
    // validator looks up rows by (DeviceType, Kind).
    public string DeviceType { get; set; } = string.Empty;

    // Vendor family bucket (cisco, juniper, nokia, arista, huawei,
    // fortinet, linux, generic). Pure metadata for the UI's grouping
    // and the validator's "did you mean" suggestions across sibling
    // device_types in the same family. Mirrors the VENDOR_FAMILY map in
    // deploy/python/flow_weaver_ssh_parsers.py.
    public string VendorFamily { get; set; } = string.Empty;

    // "exact" or "pattern". Discriminates how Value is interpreted.
    public string Kind { get; set; } = KindExact;

    // For Kind="exact": the literal command (`show version`).
    // For Kind="pattern": a regex evaluated against the normalised
    //   (trimmed, whitespace-collapsed, lowercased) command.
    public string Value { get; set; } = string.Empty;

    // Free-form note shown in the admin UI — typical use: "added after
    // ticket #123 to allow show isis adjacency on cisco_xr".
    public string? Notes { get; set; }

    // Human-readable purpose of the command ("LLDP neighbor table",
    // "running-config dump"). This is what find_command matches a user's
    // task against to retrieve the right command for a vendor, instead of
    // the agent guessing CLI syntax from memory. Nullable: the in-code
    // baseline may not carry it yet; Skills/vendors/*.yaml `description`
    // populates it (and backfills existing rows).
    public string? Description { get; set; }

    // Risk classification mirrored from the YAML `intent` field:
    // "read" | "write" | "disruptive". Advisory metadata for the agent
    // and the admin UI — the Policy gate (ssh_command_regex) still owns
    // enforcement. Nullable when a row predates this column.
    public string? Intent { get; set; }

    // "seed" = inserted by DefaultVendorCommandsSeedService at first
    // boot. "user" = inserted via the API/UI. Drives whether the
    // re-seed loop touches the row (seed rows are idempotent;
    // user rows are never overwritten).
    public string Source { get; set; } = SourceUser;

    public const string KindExact = "exact";
    public const string KindPattern = "pattern";
    public const string SourceSeed = "seed";
    public const string SourceUser = "user";

    public const string IntentRead = "read";
    public const string IntentWrite = "write";
    public const string IntentDisruptive = "disruptive";
}
