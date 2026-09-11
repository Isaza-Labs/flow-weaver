namespace flow_weaver_backend.Utils.Report;

// Shared palette + typography constants. Mirrors the :root custom
// properties from reporte.html so the HTML/PDF exporters produce the
// same visual language. Values are hex strings; exporters convert to
// whatever their underlying engine expects.
//
// This is intentionally not injected — it's pure data, no dependencies,
// reusable across the four exporters without touching DI.
public static class ReportTheme
{
    // ─── Base surfaces + text ───────────────────────────────────────────

    public const string Bg = "#f8f9fc";
    public const string Surface = "#ffffff";
    public const string SurfaceAlt = "#f1f3f9";
    public const string Border = "#e2e5ef";
    public const string BorderFocus = "#6366f1";
    public const string Text = "#1e1e2e";
    public const string TextMuted = "#64668b";
    public const string TextLight = "#9496b8";

    // ─── Accent (primary) ───────────────────────────────────────────────

    public const string Accent = "#6366f1";
    public const string AccentLight = "#eef2ff";
    public const string AccentDark = "#4f46e5";

    // ─── Severity palette ───────────────────────────────────────────────

    public const string Green = "#10b981";
    public const string GreenBg = "#ecfdf5";
    public const string GreenBorder = "#a7f3d0";

    public const string Orange = "#f59e0b";
    public const string OrangeBg = "#fffbeb";
    public const string OrangeBorder = "#fde68a";

    public const string Red = "#ef4444";
    public const string RedBg = "#fef2f2";
    public const string RedBorder = "#fecaca";
    public const string RedDark = "#b91c1c";
    public const string RedDarkBg = "#fff1f2";

    public const string Blue = "#3b82f6";
    public const string BlueBg = "#eff6ff";
    public const string BlueBorder = "#bfdbfe";

    public const string Purple = "#8b5cf6";
    public const string PurpleBg = "#f5f3ff";
    public const string PurpleBorder = "#ddd6fe";

    public const string Teal = "#14b8a6";
    public const string TealBg = "#f0fdfa";
    public const string TealBorder = "#99f6e4";

    public const string Cyan = "#06b6d4";
    public const string CyanBg = "#ecfeff";
    public const string CyanBorder = "#a5f3fc";

    // ─── Typography ─────────────────────────────────────────────────────

    public const string FontFamily = "'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";
    public const string FontFamilyMono = "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace";

    // ─── Tone resolvers ─────────────────────────────────────────────────

    /// <summary>
    /// Main foreground color for a `tone` string. Unknown tones fall back
    /// to the neutral text color so an agent-fabricated tone never breaks
    /// the render.
    /// </summary>
    public static string ToneColor(string? tone) => (tone ?? "neutral").ToLowerInvariant() switch
    {
        "critical" => RedDark,
        "high" => Red,
        "medium" => Orange,
        "low" => Blue,
        "ok" or "success" => Green,
        "accent" => Accent,
        "info" => Blue,
        "warn" or "warning" => Orange,
        "danger" or "error" => Red,
        _ => Text,
    };

    /// <summary>Light tinted background for a tone (used behind sev-badges + callouts).</summary>
    public static string ToneBackground(string? tone) => (tone ?? "neutral").ToLowerInvariant() switch
    {
        "critical" => RedDarkBg,
        "high" => RedBg,
        "medium" => OrangeBg,
        "low" => BlueBg,
        "ok" or "success" => GreenBg,
        "accent" => AccentLight,
        "info" => BlueBg,
        "warn" or "warning" => OrangeBg,
        "danger" or "error" => RedBg,
        _ => SurfaceAlt,
    };

    /// <summary>Border color matching the tinted background.</summary>
    public static string ToneBorder(string? tone) => (tone ?? "neutral").ToLowerInvariant() switch
    {
        "critical" => RedBorder,
        "high" => RedBorder,
        "medium" => OrangeBorder,
        "low" => BlueBorder,
        "ok" or "success" => GreenBorder,
        "accent" => BorderFocus,
        "info" => BlueBorder,
        "warn" or "warning" => OrangeBorder,
        "danger" or "error" => RedBorder,
        _ => Border,
    };

    /// <summary>Category tint for section headers (maps .cat-* in the HTML).</summary>
    public static (string fg, string bg, string border) Category(string? category) =>
        (category ?? "").ToLowerInvariant() switch
        {
            "critical" => (RedDark, RedDarkBg, RedBorder),
            "api" => (Blue, BlueBg, BlueBorder),
            "ai" => (Purple, PurpleBg, PurpleBorder),
            "engine" => (Green, GreenBg, GreenBorder),
            "store" => (Teal, TealBg, TealBorder),
            "worker" => (Orange, OrangeBg, OrangeBorder),
            "scheduler" => (Cyan, CyanBg, CyanBorder),
            "frontend" => (Accent, AccentLight, BorderFocus),
            _ => (Accent, AccentLight, BorderFocus),
        };
}
