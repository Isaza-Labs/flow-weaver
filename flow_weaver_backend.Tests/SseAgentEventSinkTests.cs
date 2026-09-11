using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Ai.Tools;
using Microsoft.AspNetCore.Http;

namespace flow_weaver_backend.Tests;

// The SSE wire format the chat frontend parses (ai-stream.ts). Every shape here
// is a contract with client code that ships separately, so a "harmless"
// rename silently breaks the live chat for anyone on the old bundle.
//
// The two behaviours worth guarding beyond shape: previews are bounded (an
// unbounded tool payload pushes the browser's message list past DOM limits),
// and an attachment parse error must never break the stream — the agent's text
// still has to reach the user.
public class SseAgentEventSinkTests
{
    private sealed class Capture
    {
        public DefaultHttpContext Http { get; } = new();
        private readonly MemoryStream _body = new();

        public Capture() => Http.Response.Body = _body;

        public SseAgentEventSink Sink() => new(Http.Response);

        public string Raw => Encoding.UTF8.GetString(_body.ToArray());

        // Each SSE frame is `data: <json>\n\n`. Only the leading prefix is
        // stripped — a payload can legitimately contain the text "data: ".
        public List<JsonElement> Frames => Raw
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.StartsWith("data: ", StringComparison.Ordinal) ? f["data: ".Length..] : f)
            .Select(f => JsonDocument.Parse(f).RootElement.Clone())
            .ToList();

        public JsonElement Single => Assert.Single(Frames);
    }

    private static ToolCallOutput Output(string json, bool success = true)
        => new() { Success = success, Result = TestJson.Element(json) };

    // ─── Frame shapes ───────────────────────────────────────────────────

    [Fact]
    public async Task Conversation_CarriesTheIdAndWhetherItIsNew()
    {
        var c = new Capture();
        var id = Guid.NewGuid();

        await c.Sink().ConversationAsync(id, isNew: true, default);

        Assert.Equal("conversation", c.Single.GetProperty("type").GetString());
        Assert.Equal(id.ToString(), c.Single.GetProperty("id").GetString());
        Assert.True(c.Single.GetProperty("is_new").GetBoolean());
    }

    [Fact]
    public async Task Conversation_AnExistingConversationIsFlaggedAsNotNew()
    {
        var c = new Capture();

        await c.Sink().ConversationAsync(Guid.NewGuid(), isNew: false, default);

        Assert.False(c.Single.GetProperty("is_new").GetBoolean());
    }

    [Fact]
    public async Task Text_CarriesTheDeltaVerbatim()
    {
        var c = new Capture();

        await c.Sink().TextAsync("hola", default);

        Assert.Equal("text", c.Single.GetProperty("type").GetString());
        Assert.Equal("hola", c.Single.GetProperty("content").GetString());
    }

    // A delta containing a quote, a newline or a lone `data:` prefix must not
    // break frame parsing on the client.
    [Theory]
    [InlineData("he said \"hi\"")]
    [InlineData("line one\nline two")]
    [InlineData("data: not a frame")]
    [InlineData("back\\slash")]
    public async Task Text_SpecialCharactersAreEscapedNotEmitted(string delta)
    {
        var c = new Capture();

        await c.Sink().TextAsync(delta, default);

        Assert.Equal(delta, c.Single.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ToolStart_CarriesTheNameAndAnArgumentPreview()
    {
        var c = new Capture();

        await c.Sink().ToolStartAsync("run_workflow", TestJson.Element("""{"id":"7"}"""), default);

        Assert.Equal("tool_start", c.Single.GetProperty("type").GetString());
        Assert.Equal("run_workflow", c.Single.GetProperty("name").GetString());
        Assert.Equal("7", c.Single.GetProperty("args_preview").GetProperty("id").GetString());
    }

    // Beyond the cap the preview degrades to a truncated STRING, so the client
    // renders it as text instead of trying to read fields that aren't there.
    [Fact]
    public async Task ToolStart_AnOversizedArgumentBlobIsTruncated()
    {
        var c = new Capture();
        var big = JsonSerializer.Serialize(new { blob = new string('x', 2000) });

        await c.Sink().ToolStartAsync("run_workflow", TestJson.Element(big), default);

        var preview = c.Single.GetProperty("args_preview");
        Assert.Equal(JsonValueKind.String, preview.ValueKind);
        Assert.Contains("truncated", preview.GetString());
    }

    [Fact]
    public async Task ToolResult_CarriesSuccessAndAPreview()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("list_workflows", Output("""{"count":2}"""), default);

        Assert.Equal("tool_result", c.Single.GetProperty("type").GetString());
        Assert.True(c.Single.GetProperty("success").GetBoolean());
        Assert.Equal(2, c.Single.GetProperty("preview").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ToolResult_AFailedToolIsFlagged()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("list_workflows", Output("""{"error":"nope"}""", success: false), default);

        Assert.False(c.Single.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ToolResult_AnOversizedResultIsTruncated()
    {
        var c = new Capture();
        var big = JsonSerializer.Serialize(new { blob = new string('x', 3000) });

        await c.Sink().ToolResultAsync("list_workflows", Output(big), default);

        Assert.Contains("truncated", c.Single.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task Done_CarriesTheTokenAccounting()
    {
        var c = new Capture();

        await c.Sink().DoneAsync(tokensIn: 120, tokensOut: 45, iterations: 3, default);

        Assert.Equal("done", c.Single.GetProperty("type").GetString());
        Assert.Equal(120, c.Single.GetProperty("tokens_in").GetInt32());
        Assert.Equal(45, c.Single.GetProperty("tokens_out").GetInt32());
        Assert.Equal(3, c.Single.GetProperty("iterations").GetInt32());
    }

    // A timeout still hands back whatever text was produced, so the user sees
    // a partial answer rather than a blank turn.
    [Fact]
    public async Task Timeout_CarriesTheDeadlineAndThePartialTail()
    {
        var c = new Capture();

        await c.Sink().TimeoutAsync("...as I was saying", deadlineSeconds: 60, default);

        Assert.Equal("timeout", c.Single.GetProperty("type").GetString());
        Assert.Equal(60, c.Single.GetProperty("deadline_s").GetInt32());
        Assert.Equal("...as I was saying", c.Single.GetProperty("partial").GetString());
    }

    [Fact]
    public async Task Error_CarriesTheMessageAndOptionalCode()
    {
        var c = new Capture();

        await c.Sink().ErrorAsync("provider unreachable", "provider_down", default);

        Assert.Equal("error", c.Single.GetProperty("type").GetString());
        Assert.Equal("provider unreachable", c.Single.GetProperty("message").GetString());
        Assert.Equal("provider_down", c.Single.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Error_TheCodeKeyIsOmittedWhenThereIsNone()
    {
        var c = new Capture();

        await c.Sink().ErrorAsync("boom", null, default);

        Assert.False(c.Single.TryGetProperty("code", out _));
    }

    // ─── Attachments ────────────────────────────────────────────────────

    private const string ReportResult = """
        {"report_artifact_id":"11111111-1111-1111-1111-111111111111","filename":"audit.pdf",
         "content_type":"application/pdf","format":"pdf","size_bytes":2048,"base64":"AAAA"}
        """;

    // A generated report gets a download link the UI can render without
    // re-parsing the (possibly truncated) preview.
    [Fact]
    public async Task Attachment_AGeneratedReportGetsADownloadLink()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("generate_report", Output(ReportResult), default);

        var attachment = c.Single.GetProperty("attachment");
        Assert.Equal("report", attachment.GetProperty("kind").GetString());
        Assert.Equal("audit.pdf", attachment.GetProperty("filename").GetString());
        Assert.Equal("application/pdf", attachment.GetProperty("content_type").GetString());
        Assert.Equal(2048, attachment.GetProperty("size_bytes").GetInt32());
        Assert.Equal("/api/reports/11111111-1111-1111-1111-111111111111/download",
            attachment.GetProperty("download_url").GetString());
    }

    // The base64 body is persisted server-side; echoing it into the event
    // stream would multiply the payload for no benefit.
    [Fact]
    public async Task Attachment_TheBase64BodyIsNotEchoedIntoTheAttachment()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("generate_report", Output(ReportResult), default);

        Assert.False(c.Single.GetProperty("attachment").TryGetProperty("base64", out _));
    }

    [Fact]
    public async Task Attachment_ToolNameMatchingIsCaseInsensitive()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("GENERATE_REPORT", Output(ReportResult), default);

        Assert.True(c.Single.TryGetProperty("attachment", out _));
    }

    [Fact]
    public async Task Attachment_OtherToolsGetNoAttachmentKey()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("list_workflows", Output(ReportResult), default);

        Assert.False(c.Single.TryGetProperty("attachment", out _));
    }

    [Fact]
    public async Task Attachment_AFailedReportToolGetsNoAttachment()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("generate_report", Output(ReportResult, success: false), default);

        Assert.False(c.Single.TryGetProperty("attachment", out _));
    }

    // A malformed or partial result must not break the stream — the frame
    // still lands, just without the link.
    [Theory]
    [InlineData("""{"filename":"audit.pdf"}""")]
    [InlineData("""{"report_artifact_id":"abc"}""")]
    [InlineData("""{"report_artifact_id":123,"filename":"a.pdf"}""")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    public async Task Attachment_AnUnusableResultStillEmitsTheFrame(string result)
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("generate_report", Output(result), default);

        Assert.Equal("tool_result", c.Single.GetProperty("type").GetString());
        Assert.False(c.Single.TryGetProperty("attachment", out _));
    }

    // A missing size is tolerated: the link still works, the UI just can't
    // show the file size.
    [Fact]
    public async Task Attachment_AMissingSizeIsTolerated()
    {
        var c = new Capture();

        await c.Sink().ToolResultAsync("generate_report", Output(
            """{"report_artifact_id":"abc","filename":"a.pdf"}"""), default);

        var attachment = c.Single.GetProperty("attachment");
        Assert.Equal("a.pdf", attachment.GetProperty("filename").GetString());
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("size_bytes").ValueKind);
    }

    // ─── Framing ────────────────────────────────────────────────────────

    // Every event is one `data: …\n\n` frame; a missing blank line would make
    // the client coalesce two events into one.
    [Fact]
    public async Task Framing_EachEventIsItsOwnDataFrame()
    {
        var c = new Capture();
        var sink = c.Sink();

        await sink.TextAsync("one", default);
        await sink.TextAsync("two", default);
        await sink.DoneAsync(1, 1, 1, default);

        Assert.Equal(3, c.Frames.Count);
        Assert.StartsWith("data: ", c.Raw);
        Assert.EndsWith("\n\n", c.Raw);
    }
}
