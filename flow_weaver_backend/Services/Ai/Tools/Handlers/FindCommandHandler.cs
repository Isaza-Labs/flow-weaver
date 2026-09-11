using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// AI tool that turns a vendor-neutral TASK ("show LLDP neighbors",
// "dump running-config") into the concrete command(s) for a device_type,
// by ranking the vendor_commands catalog against the task
// wording. This is the proactive counterpart to validate_ssh_commands
// (which only says yes/no on a command the agent already guessed): for
// non-Cisco vendors the agent should retrieve the command here instead
// of transposing IOS syntax. Ranking is keyword overlap over each
// command's Value + Description — no embeddings, fully deterministic.
public sealed class FindCommandHandler : IToolHandler
{
    public string Name => "find_command";

    public string Description =>
        "Retrieve the canonical CLI command(s) for a device_type from the vendor_commands " +
        "catalog by describing the TASK in plain words (e.g. 'show LLDP " +
        "neighbors', 'dump running-config', 'configure SNMP community') instead of guessing " +
        "vendor syntax. Returns ranked commands with their purpose and risk " +
        "(read/write/disruptive). For a CONFIGURATION task pass intent='write' so you get " +
        "config commands, not read-only ones. Call this BEFORE drafting an ssh node for any " +
        "non-cisco_ios/linux device.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["device_type", "task"],
          "properties": {
            "device_type": {
              "type": "string",
              "description": "Netmiko device_type (cisco_ios, juniper_junos, nokia_srl, ...)."
            },
            "task": {
              "type": "string",
              "description": "What you want to do, vendor-neutral: 'show LLDP neighbors', 'dump running-config', 'show BGP summary'."
            },
            "limit": {
              "type": "integer",
              "description": "Max candidate commands to return (1-20, default 8)."
            },
            "intent": {
              "type": "string",
              "enum": ["read", "write", "disruptive"],
              "description": "Optional risk filter. Pass 'write' for configuration tasks so you get config commands instead of read-only ones."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IVendorCommandRepository _vendorCommands;
    private readonly ICurrentUser _caller;
    private readonly ILogger<FindCommandHandler> _logger;

    public FindCommandHandler(IVendorCommandRepository vendorCommands, ICurrentUser caller, ILogger<FindCommandHandler> logger)
    {
        _vendorCommands = vendorCommands;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var deviceType = GetString(args, "device_type")?.Trim() ?? string.Empty;
        var task = GetString(args, "task")?.Trim() ?? string.Empty;
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv)
            ? Math.Clamp(lv, 1, 20)
            : 8;

        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(task))
        {
            return JsonSerializer.SerializeToElement(new
            {
                found = false,
                warnings = new[] { "device_type and task are both required." },
                candidates = Array.Empty<object>(),
            });
        }

        // Only exact rows are retrievable as a concrete command; patterns
        // (e.g. ^show\s+\S+...) describe a family, not a runnable line.
        var rows = await _vendorCommands.ListActiveExactForRankingAsync(deviceType, ct);

        if (rows.Count == 0)
        {
            _logger.LogInformation("ai.tool.find_command.empty device_type={DeviceType}", deviceType);
            return JsonSerializer.SerializeToElement(new
            {
                found = false,
                device_type = deviceType,
                task,
                candidates = Array.Empty<object>(),
                catalog_total = 0,
                hint = $"No catalog entries for device_type '{deviceType}'. Check the device_type, or add commands via /api/vendor-command.",
            });
        }

        var explicitIntent = GetString(args, "intent")?.Trim().ToLowerInvariant();
        // Is this a configuration (write) task? Explicit intent wins;
        // otherwise sniff the task for config verbs. Read/ambiguous tasks
        // aren't filtered — reads are the safe default.
        var wantIntents = explicitIntent switch
        {
            "write" => new HashSet<string> { "write", "disruptive" },
            "disruptive" => new HashSet<string> { "disruptive" },
            "read" => new HashSet<string> { "read" },
            _ => LooksLikeWrite(task) ? new HashSet<string> { "write", "disruptive" } : null,
        };
        var writeTask = wantIntents is not null && wantIntents.Contains("write");

        var taskTokens = Tokenize(task);
        var ranked = rows
            .Select(r =>
            {
                var rowTokens = Tokenize(r.Value + " " + (r.Description ?? string.Empty));
                var score = taskTokens.Count(t => rowTokens.Contains(t));
                return new { r.Value, r.Description, r.Intent, score };
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.Value.Length)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // For an intent-filtered task, only consider commands of that risk
        // class — rows with no intent are excluded because we can't vouch
        // they're safe writes. Unfiltered (read/ambiguous) tasks see all.
        var pool = wantIntents is null
            ? ranked
            : ranked.Where(x => x.Intent != null && wantIntents.Contains(x.Intent.ToLowerInvariant())).ToList();

        var matched = pool.Where(x => x.score > 0).Take(limit).ToList();
        var semantic = matched.Count > 0;

        var chosen = matched;
        string? hint = null;
        if (!semantic)
        {
            if (writeTask)
            {
                // Never fall back to read commands for a config task — an
                // irrelevant read result is exactly what nudged the agent
                // to guess IOS syntax. Be explicit instead.
                chosen = pool.Take(limit).ToList();
                hint = pool.Count == 0
                    ? $"This is a configuration (write) task but the catalog has NO write commands for device_type '{deviceType}'. Do NOT invent config syntax — retrieve it from the vendor's docs or ask an admin to add the config commands to vendor_commands."
                    : "This is a configuration (write) task; none of the catalog's write commands matched the wording. Pick from these or refine the task. Do NOT invent config syntax.";
            }
            else
            {
                // No keyword overlap on a read/ambiguous task: a short
                // browse slice beats an empty result that invites guessing.
                chosen = ranked.Take(limit).ToList();
                hint = "No command matched the task wording; these are sample catalog entries. Refine the task or call list_vendor_commands for the full catalog.";
            }
        }

        _logger.LogInformation(
            "ai.tool.find_command.ok device_type={DeviceType} write_task={WriteTask} semantic={Semantic} returned={Returned} catalog_total={Total}",
            deviceType, writeTask, semantic, chosen.Count, rows.Count);

        return JsonSerializer.SerializeToElement(new
        {
            found = chosen.Count > 0,
            device_type = deviceType,
            task,
            intent = wantIntents is null ? null : (explicitIntent ?? "write"),
            semantic_match = semantic,
            candidates = chosen.Select(x => new
            {
                command = x.Value,
                description = x.Description,
                intent = x.Intent,
                score = x.score,
            }),
            catalog_total = rows.Count,
            hint,
        });
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "for", "on", "to", "in", "and", "or", "me", "my",
        "i", "please", "want", "need", "see", "give", "with", "that", "this",
        "show", "display", "get", "list", "view", "read", "fetch", "retrieve", "print", "output",
    };

    // Lowercase alphanumeric word tokens, minus CLI-generic verbs and
    // stop-words, so the score reflects the *technical* terms (lldp, bgp,
    // running, config) the task and command share — not filler like "show".
    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return set;
        foreach (Match m in Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+"))
        {
            var t = m.Value;
            if (t.Length < 2 || Stop.Contains(t)) continue;
            set.Add(t);
        }
        return set;
    }

    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "configure", "config", "set", "enable", "disable", "add", "create",
        "remove", "delete", "change", "update", "apply", "commit", "provision",
        "push", "modify", "unset",
    };

    // Heuristic: does the task ask to CHANGE config (vs read it)? Used to
    // bias find_command toward write commands and to refuse a read-only
    // fallback for a config task.
    private static bool LooksLikeWrite(string task)
    {
        foreach (Match m in Regex.Matches(task.ToLowerInvariant(), "[a-z0-9]+"))
            if (WriteVerbs.Contains(m.Value)) return true;
        return false;
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
