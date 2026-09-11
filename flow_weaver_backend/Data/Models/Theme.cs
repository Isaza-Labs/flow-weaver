using System.Text.Json;

namespace flow_weaver_backend.Models;

// A user-authored colour theme, selectable from the theme picker alongside
// the seven built-in `fw-*` themes.
//
// Only the BASE colour of each palette is stored — one hex per palette. The
// frontend generates the 50→950 ramp and the contrast tokens from it
// (`$lib/theme/ramp.ts`), which is what makes a colour picker a usable editor:
// nobody hand-picks 77 shades. Storing the ramp instead would freeze the
// generation curve into every saved row.
//
// Visibility has two levels, deliberately not a role:
//   IsShared = true   → every authenticated user sees it in the picker.
//                       Publishing one is an admin action.
//   IsShared = false  → only OwnerUserId sees it. Any user may create these.
// OwnerUserId is always the author, shared or not, so a published theme still
// names who is responsible for it.
public class Theme : BaseModel
{
    // Palette names a theme may define. A theme need not define all of them —
    // whatever it omits keeps the FlowWeaver brand value from `_palette.css`,
    // so "just recolour the primary" is a one-key theme.
    public static readonly IReadOnlySet<string> PaletteKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "primary", "secondary", "tertiary", "success", "warning", "error", "surface",
    };

    // Non-colour style knobs a theme may set. Keys drawn from SettingKeys;
    // every one is optional, and a theme with none behaves exactly like a
    // colours-only theme. Values are validated per key in ThemeService
    // (numeric ranges for the scales, fixed vocabularies for the fonts).
    public static readonly IReadOnlySet<string> SettingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "roundness",       // corner radius multiplier over the app defaults
        "ui_scale",        // root font-size multiplier — rem-based sizing follows
        "font_body",       // body font stack, from a fixed vocabulary
        "font_heading",    // heading font stack, or "inherit" to follow the body
        "font_mono",       // code font stack
        "heading_weight",  // heading font-weight
    };

    public Guid ThemeId { get; set; }

    public string Name { get; set; } = string.Empty;

    // Shown under the name in the picker, same as the built-in themes.
    public string? Description { get; set; }

    // `{ "primary": "#5c69ab", "surface": "#1c1f2b", … }` — hex, one per
    // palette, keys drawn from PaletteKeys. jsonb, so adding a palette later
    // needs no migration.
    public JsonElement Colors { get; set; }

    // `{ "roundness": 1.5, "font_body": "serif", … }` — keys drawn from
    // SettingKeys. Null means the theme predates settings or never set any;
    // both read as "inherit every app default".
    public JsonElement? Settings { get; set; }

    // Published to everyone (admin-only) vs private to the author.
    public bool IsShared { get; set; }

    // The author. Never null in practice; nullable so a user delete doesn't
    // cascade away a published theme the rest of the org is using.
    public Guid? OwnerUserId { get; set; }
}
