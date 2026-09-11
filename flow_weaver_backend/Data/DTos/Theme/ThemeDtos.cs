using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos.Themes;

// `colors` is a flat map of palette → base hex, e.g.
//   { "primary": "#5c69ab", "surface": "#1c1f2b" }
// Keys must come from Theme.PaletteKeys; omitted palettes keep the FlowWeaver
// brand value. The 50→950 ramp is generated in the browser, not stored.
public class CreateTheme
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("colors")]
    public JsonElement Colors { get; set; }

    // Optional style knobs (`roundness`, `ui_scale`, `font_body`, …) — see
    // Theme.SettingKeys. Omitted or empty means "inherit every app default".
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    // Publish to every user. Admin-only; a non-admin asking for it is a 403,
    // not a silent downgrade to private.
    [JsonPropertyName("is_shared")]
    public bool IsShared { get; set; }
}

// Every field optional — a PATCH-shaped update. `colors` replaces the whole
// map rather than merging: a partial merge would make "remove the surface
// override" impossible to express.
public class UpdateTheme
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("colors")]
    public JsonElement? Colors { get; set; }

    // Like `colors`, replaces the whole map. An explicit empty object clears
    // every style override back to the app defaults.
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    [JsonPropertyName("is_shared")]
    public bool? IsShared { get; set; }
}

public class ThemeResponse
{
    [JsonPropertyName("theme_id")]
    public Guid ThemeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("colors")]
    public JsonElement Colors { get; set; }

    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    [JsonPropertyName("is_shared")]
    public bool IsShared { get; set; }

    [JsonPropertyName("owner_user_id")]
    public Guid? OwnerUserId { get; set; }

    // True when the caller may edit or delete this row — the picker uses it to
    // decide whether to show the edit affordances, so the UI never offers an
    // action the API will refuse.
    [JsonPropertyName("can_edit")]
    public bool CanEdit { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
