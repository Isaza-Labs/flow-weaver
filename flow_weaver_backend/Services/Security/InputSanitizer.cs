using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Security;

// Strips control characters (except newline/tab/carriage return) from
// user-provided strings. Called at the DTO boundary to ensure DB rows
// never contain invisible control chars that could confuse UI rendering
// or downstream parsers.
//
// Also exposes ANSI-stripping helpers used by handlers (e.g. SSH) and
// the runtime template resolver: terminal devices like Nokia SR-OS and
// Cisco IOS-XR with paging emit color/cursor-move escapes that pollute
// downstream consumers (NetBox custom_fields, email body, Slack).
public static partial class InputSanitizer
{
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlChars();

    // CSI: ESC '[' params intermediate final-byte (0x40..0x7E) — colors,
    // cursor moves, erase. Most common offender in router output.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiCsi();

    // OSC: ESC ']' payload (BEL or ST) — window titles, hyperlinks.
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex AnsiOsc();

    // Single-shift / charset switches and other 7-bit ESC sequences.
    [GeneratedRegex(@"\x1B[@-Z\\-_]")]
    private static partial Regex AnsiOther();

    // C0 controls + DEL, keeping \t (0x09) \n (0x0A) \r (0x0D).
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex C0Controls();

    public static string Sanitize(string? input, int maxLength = 255)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var cleaned = ControlChars().Replace(input, string.Empty);
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    public static string SanitizeLong(string? input, int maxLength = 10_000) =>
        Sanitize(input, maxLength);

    // Removes ANSI CSI/OSC and other 7-bit ESC sequences. Leaves all
    // printable text and whitespace untouched. Idempotent.
    public static string StripAnsi(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var s = AnsiCsi().Replace(input, string.Empty);
        s = AnsiOsc().Replace(s, string.Empty);
        s = AnsiOther().Replace(s, string.Empty);
        return s;
    }

    // Strips ANSI escapes AND C0 control chars (keeps \t \n \r). Use for
    // payloads that flow into downstream stores (NetBox, email, Slack)
    // where invisible chars cause silent data corruption.
    public static string StripAnsiAndControl(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var s = StripAnsi(input);
        return C0Controls().Replace(s, string.Empty);
    }
}
