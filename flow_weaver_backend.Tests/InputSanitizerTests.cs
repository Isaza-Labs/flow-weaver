using flow_weaver_backend.Services.Security;

namespace flow_weaver_backend.Tests;

// Covers the ANSI/control-char stripping helpers used by SSH output
// post-processing and (eventually) by the runtime template `| strip_ansi`
// filter. The Sanitize/SanitizeLong tests guard the existing user-input
// path that already shipped.
public class InputSanitizerTests
{
    [Fact]
    public void StripAnsi_removes_csi_color_codes()
    {
        var raw = "\x1b[0mhello\x1b[31mworld\x1b[0m";
        Assert.Equal("helloworld", InputSanitizer.StripAnsi(raw));
    }

    [Fact]
    public void StripAnsi_removes_cursor_moves()
    {
        // Nokia SR-OS commonly emits this — the original failure mode.
        var raw = "\x1b[0m--{ running }--[ ]--\x1b[0m \x1b[0mA:node-l1# show version\x1b[23D\x1b[0m";
        var clean = InputSanitizer.StripAnsi(raw);
        Assert.False(clean.Contains('\x1b'), "ESC chars must be stripped");
        Assert.Equal("--{ running }--[ ]-- A:node-l1# show version", clean);
    }

    [Fact]
    public void StripAnsi_removes_osc_sequences()
    {
        var raw = "\x1b]0;Window Title\x07hello";
        Assert.Equal("hello", InputSanitizer.StripAnsi(raw));
    }

    [Fact]
    public void StripAnsi_is_idempotent()
    {
        var raw = "\x1b[1mfoo\x1b[0m";
        var once = InputSanitizer.StripAnsi(raw);
        var twice = InputSanitizer.StripAnsi(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void StripAnsi_preserves_printable_and_whitespace()
    {
        var raw = "line1\nline2\twith tab\rcarriage";
        Assert.Equal(raw, InputSanitizer.StripAnsi(raw));
    }

    [Fact]
    public void StripAnsiAndControl_removes_c0_chars_but_keeps_tnr()
    {
        var raw = "before\x00\x07\x08mid\nend\twith\rcr";
        var clean = InputSanitizer.StripAnsiAndControl(raw);
        Assert.Equal("beforemid\nend\twith\rcr", clean);
    }

    [Fact]
    public void StripAnsi_handles_empty_and_null()
    {
        Assert.Equal(string.Empty, InputSanitizer.StripAnsi(null));
        Assert.Equal(string.Empty, InputSanitizer.StripAnsi(string.Empty));
        Assert.Equal(string.Empty, InputSanitizer.StripAnsiAndControl(null));
    }
}
