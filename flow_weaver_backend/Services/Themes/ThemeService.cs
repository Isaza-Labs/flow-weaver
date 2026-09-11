using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Themes;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Themes;

// CRUD for user-authored colour themes.
//
// Unlike most services here the controller is NOT admin-gated — any
// authenticated user may create a private theme for themselves. The privileged
// action is *publishing* one (`is_shared`), which repaints the picker for the
// whole org, so that single flag is the thing this service guards. Everything
// else is ownership: you edit what you authored, an admin edits what is
// published.
public interface IThemeService
{
    Task<ActionResult<ListResponse<ThemeResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<ThemeResponse>> GetAsync(Guid id);
    Task<ActionResult<ThemeResponse>> CreateAsync(CreateTheme dto);
    Task<ActionResult<ThemeResponse>> UpdateAsync(Guid id, UpdateTheme dto);
    Task<IActionResult> DeleteAsync(Guid id);
}

public partial class ThemeService : IThemeService
{
    // #rrggbb only. Short (#abc), 8-digit (#rrggbbaa) and named colours are
    // rejected: the ramp generator parses exactly this, and a colour it can't
    // parse would render as an invalid CSS token that silently falls back to
    // the inherited palette — a theme that "saves fine" and then looks wrong.
    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled)]
    private static partial Regex HexRe();

    private const int MaxNameLength = 60;
    private const int MaxDescriptionLength = 200;

    private readonly IThemeRepository _themes;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;

    public ThemeService(IThemeRepository themes, ICurrentUser caller, IAuditLogger audit)
    {
        _themes = themes;
        _caller = caller;
        _audit = audit;
    }

    private bool IsAdmin =>
        _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    // You may edit your own theme; an admin may also edit any published one.
    // A published theme authored by someone else is read-only to a non-admin
    // even though they can see it.
    private bool CanEdit(Theme t) =>
        (t.OwnerUserId is { } owner && owner == _caller.UserId) || (IsAdmin && t.IsShared);

    public async Task<ActionResult<ListResponse<ThemeResponse>>> ListAsync(int limit, int offset)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var userId = _caller.UserId;
        var total = await _themes.CountVisibleToAsync(userId);
        var rows = await _themes.ListVisibleToAsync(userId, limit, offset);
        return new OkObjectResult(new ListResponse<ThemeResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<ThemeResponse>> GetAsync(Guid id)
    {
        var theme = await _themes.GetByIdAsync(id, activeOnly: true, tracking: false)
            ?? throw new NotFoundException("theme", id);
        // Someone else's private theme is invisible, not forbidden — 404 keeps
        // the existence of another user's themes out of the response.
        if (!theme.IsShared && theme.OwnerUserId != _caller.UserId)
            throw new NotFoundException("theme", id);
        return ToResponse(theme);
    }

    public async Task<ActionResult<ThemeResponse>> CreateAsync(CreateTheme dto)
    {
        var name = ValidateName(dto.Name);
        var description = ValidateDescription(dto.Description);
        var colors = ValidateColors(dto.Colors);
        var settings = ValidateSettings(dto.Settings);

        if (dto.IsShared && !IsAdmin)
            throw new ForbiddenException(
                "publishing a theme to every user is an admin action — save it as a private theme instead.",
                code: "theme_share_forbidden");

        var owner = _caller.UserId;
        if (await _themes.FindByNameAsync(name, dto.IsShared, owner) is not null)
            throw new ValidationException(
                dto.IsShared
                    ? $"a shared theme named '{name}' already exists."
                    : $"you already have a theme named '{name}'.",
                code: "theme_name_duplicate");

        var now = DateTime.UtcNow;
        var theme = new Theme
        {
            ThemeId = Guid.NewGuid(),
            Name = name,
            Description = description,
            Colors = colors,
            Settings = settings,
            IsShared = dto.IsShared,
            OwnerUserId = owner,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _themes.Add(theme);
        await _themes.SaveChangesAsync();

        // Only publishing is worth an audit row — a private theme is a personal
        // preference, and logging every colour tweak would drown the log.
        if (theme.IsShared)
            await _audit.LogAsync("theme", theme.ThemeId, "publish",
                after: new { theme.Name, theme.IsShared });

        return ToResponse(theme);
    }

    public async Task<ActionResult<ThemeResponse>> UpdateAsync(Guid id, UpdateTheme dto)
    {
        var theme = await _themes.GetByIdAsync(id, activeOnly: true, tracking: true)
            ?? throw new NotFoundException("theme", id);
        if (!theme.IsShared && theme.OwnerUserId != _caller.UserId)
            throw new NotFoundException("theme", id);
        if (!CanEdit(theme))
            throw new ForbiddenException(
                "this theme belongs to someone else — duplicate it to make your own.",
                code: "theme_edit_forbidden");

        var wasShared = theme.IsShared;

        if (dto.Name is not null)
        {
            var name = ValidateName(dto.Name);
            var targetShared = dto.IsShared ?? theme.IsShared;
            var clash = await _themes.FindByNameAsync(name, targetShared, theme.OwnerUserId);
            if (clash is not null && clash.ThemeId != theme.ThemeId)
                throw new ValidationException(
                    $"a theme named '{name}' already exists.", code: "theme_name_duplicate");
            theme.Name = name;
        }
        if (dto.Description is not null)
            theme.Description = ValidateDescription(dto.Description);
        if (dto.Colors is { } colors)
            theme.Colors = ValidateColors(colors);
        if (dto.Settings is { } settings)
            theme.Settings = ValidateSettings(settings);
        if (dto.IsShared is { } isShared && isShared != theme.IsShared)
        {
            // Both directions are privileged: publishing repaints everyone's
            // picker, unpublishing yanks a theme other people may be using.
            if (!IsAdmin)
                throw new ForbiddenException(
                    "changing whether a theme is shared is an admin action.",
                    code: "theme_share_forbidden");
            theme.IsShared = isShared;
        }

        theme.UpdatedAt = DateTime.UtcNow;
        await _themes.SaveChangesAsync();

        if (theme.IsShared || wasShared)
            await _audit.LogAsync("theme", theme.ThemeId, "update",
                before: new { Shared = wasShared },
                after: new { theme.Name, theme.IsShared });

        return ToResponse(theme);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var theme = await _themes.GetByIdAsync(id, activeOnly: true, tracking: true)
            ?? throw new NotFoundException("theme", id);
        if (!theme.IsShared && theme.OwnerUserId != _caller.UserId)
            throw new NotFoundException("theme", id);
        if (!CanEdit(theme))
            throw new ForbiddenException(
                "this theme belongs to someone else.", code: "theme_delete_forbidden");

        // Soft delete: whoever has this theme selected keeps a dangling id in
        // their localStorage, and the frontend falls back to the default theme
        // when it can't resolve one. Keeping the row makes that recoverable.
        theme.IsActive = false;
        theme.UpdatedAt = DateTime.UtcNow;
        await _themes.SaveChangesAsync();

        if (theme.IsShared)
            await _audit.LogAsync("theme", theme.ThemeId, "delete",
                before: new { theme.Name, theme.IsShared });

        return new NoContentResult();
    }

    private static string ValidateName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ValidationException("name is required.", code: "name_required");
        if (name.Length > MaxNameLength)
            throw new ValidationException(
                $"name must be {MaxNameLength} characters or fewer.", code: "name_too_long");
        return name;
    }

    private static string? ValidateDescription(string? raw)
    {
        var description = raw?.Trim();
        if (string.IsNullOrEmpty(description)) return null;
        if (description.Length > MaxDescriptionLength)
            throw new ValidationException(
                $"description must be {MaxDescriptionLength} characters or fewer.",
                code: "description_too_long");
        return description;
    }

    // Accepts a flat object of known palette → #rrggbb. Rejects unknown keys
    // rather than dropping them: a typo'd palette name would otherwise save
    // cleanly and then do nothing, which reads as "the editor is broken".
    private static JsonElement ValidateColors(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ValidationException(
                "colors must be an object of palette → hex colour.", code: "colors_invalid");

        var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in raw.EnumerateObject())
        {
            if (!Theme.PaletteKeys.Contains(prop.Name))
                throw new ValidationException(
                    $"unknown palette '{prop.Name}'. Allowed: {string.Join(", ", Theme.PaletteKeys.Order())}.",
                    code: "colors_unknown_palette");
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new ValidationException(
                    $"colors.{prop.Name} must be a string.", code: "colors_invalid");
            var hex = (prop.Value.GetString() ?? string.Empty).Trim();
            if (!HexRe().IsMatch(hex))
                throw new ValidationException(
                    $"colors.{prop.Name} must be a hex colour like #5c69ab.", code: "colors_invalid");
            cleaned[prop.Name] = hex.ToLowerInvariant();
        }

        if (cleaned.Count == 0)
            throw new ValidationException(
                "a theme must set at least one palette colour.", code: "colors_empty");

        return JsonSerializer.SerializeToElement(cleaned);
    }

    // Per-key constraints for the style settings. Numbers are ranges, fonts a
    // fixed vocabulary — anything else would let a saved theme emit arbitrary
    // CSS into every reader's page.
    private static readonly IReadOnlySet<string> BodyFonts =
        new HashSet<string>(StringComparer.Ordinal) { "inter", "system", "serif", "mono" };
    private static readonly IReadOnlySet<string> HeadingFonts =
        new HashSet<string>(StringComparer.Ordinal) { "inherit", "inter", "system", "serif", "mono" };
    private static readonly IReadOnlySet<string> MonoFonts =
        new HashSet<string>(StringComparer.Ordinal) { "jetbrains", "system" };

    // Accepts a flat object of known setting → value. Same philosophy as
    // ValidateColors: unknown keys are an error, not silently dropped. An
    // explicit empty object is valid and clears every override.
    private static JsonElement? ValidateSettings(JsonElement? raw)
    {
        if (raw is not { } settings || settings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (settings.ValueKind != JsonValueKind.Object)
            throw new ValidationException(
                "settings must be an object of setting → value.", code: "settings_invalid");

        var cleaned = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in settings.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "roundness":
                    cleaned[prop.Name] = ValidateNumber(prop, min: 0, max: 2);
                    break;
                case "ui_scale":
                    cleaned[prop.Name] = ValidateNumber(prop, min: 0.85, max: 1.15);
                    break;
                case "heading_weight":
                    cleaned[prop.Name] = (int)ValidateNumber(prop, min: 300, max: 900);
                    break;
                case "font_body":
                    cleaned[prop.Name] = ValidateChoice(prop, BodyFonts);
                    break;
                case "font_heading":
                    cleaned[prop.Name] = ValidateChoice(prop, HeadingFonts);
                    break;
                case "font_mono":
                    cleaned[prop.Name] = ValidateChoice(prop, MonoFonts);
                    break;
                default:
                    throw new ValidationException(
                        $"unknown setting '{prop.Name}'. Allowed: {string.Join(", ", Theme.SettingKeys.Order())}.",
                        code: "settings_unknown_key");
            }
        }

        return JsonSerializer.SerializeToElement(cleaned);
    }

    private static double ValidateNumber(JsonProperty prop, double min, double max)
    {
        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out var value))
            throw new ValidationException(
                $"settings.{prop.Name} must be a number.", code: "settings_invalid");
        if (value < min || value > max)
            throw new ValidationException(
                $"settings.{prop.Name} must be between {min} and {max}.", code: "settings_invalid");
        return value;
    }

    private static string ValidateChoice(JsonProperty prop, IReadOnlySet<string> allowed)
    {
        var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        if (value is null || !allowed.Contains(value))
            throw new ValidationException(
                $"settings.{prop.Name} must be one of: {string.Join(", ", allowed.Order())}.",
                code: "settings_invalid");
        return value;
    }

    private ThemeResponse ToResponse(Theme t) => new()
    {
        ThemeId = t.ThemeId,
        Name = t.Name,
        Description = t.Description,
        Colors = t.Colors,
        Settings = t.Settings,
        IsShared = t.IsShared,
        OwnerUserId = t.OwnerUserId,
        CanEdit = CanEdit(t),
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };
}
