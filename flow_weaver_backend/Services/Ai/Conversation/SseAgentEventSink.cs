using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools;

namespace flow_weaver_backend.Services.Ai.Conversation;

// Serializes agent events to an SSE (text/event-stream) response. The JSON
// shapes here are the wire contract the chat frontend parses (ai-stream.ts) —
// keep them byte-for-byte stable. Built per request by AiChatController around
// the live HttpResponse; the response headers (content-type, no-cache) are set
// by the controller before the first write.
public sealed class SseAgentEventSink : IAgentEventSink
{
    private readonly HttpResponse _response;

    public SseAgentEventSink(HttpResponse response) => _response = response;

    public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct) =>
        WriteAsync(
            "{\"type\":\"conversation\",\"id\":" + JsonSerializer.Serialize(conversationId.ToString())
            + ",\"is_new\":" + (isNew ? "true" : "false") + "}");

    public Task TextAsync(string delta, CancellationToken ct) =>
        WriteAsync($"{{\"type\":\"text\",\"content\":{JsonSerializer.Serialize(delta)}}}");

    public Task ToolStartAsync(string toolName, JsonElement arguments, CancellationToken ct) =>
        WriteAsync(
            "{\"type\":\"tool_start\",\"name\":" + JsonSerializer.Serialize(toolName)
            + ",\"args_preview\":" + TruncateJson(arguments, 400) + "}");

    public Task ToolResultAsync(string toolName, ToolCallOutput output, CancellationToken ct)
    {
        // If the tool returned a downloadable artifact (today only
        // generate_report does), attach the artifact id + filename so the chat
        // UI can render a download link without re-parsing the truncated
        // preview. The base64 body is persisted server-side; we intentionally
        // don't echo it here — the link hits /api/reports/{id}/download instead.
        var attachmentJson = BuildAttachmentJson(toolName, output);
        return WriteAsync(
            "{\"type\":\"tool_result\",\"name\":" + JsonSerializer.Serialize(toolName)
            + ",\"success\":" + (output.Success ? "true" : "false")
            + ",\"preview\":" + TruncateJson(output.Result, 600)
            + (attachmentJson is null ? "" : ",\"attachment\":" + attachmentJson)
            + "}");
    }

    public Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct) =>
        WriteAsync(
            "{\"type\":\"done\",\"tokens_in\":" + tokensIn
            + ",\"tokens_out\":" + tokensOut
            + ",\"iterations\":" + iterations + "}");

    public Task TimeoutAsync(string partialTail, int deadlineSeconds, CancellationToken ct) =>
        WriteAsync(
            "{\"type\":\"timeout\",\"deadline_s\":" + deadlineSeconds
            + ",\"partial\":" + JsonSerializer.Serialize(partialTail) + "}");

    public Task ErrorAsync(string message, string? code, CancellationToken ct) =>
        WriteAsync(
            "{\"type\":\"error\""
            + (code is null ? "" : ",\"code\":" + JsonSerializer.Serialize(code))
            + ",\"message\":" + JsonSerializer.Serialize(message) + "}");

    private async Task WriteAsync(string data)
    {
        await _response.WriteAsync($"data: {data}\n\n");
        await _response.Body.FlushAsync();
    }

    // Keeps the SSE tool_start / tool_result events bounded. Big JSON payloads
    // would bloat the wire and, worse, push the client's message list past
    // browser DOM limits.
    private static string TruncateJson(JsonElement value, int maxChars)
    {
        var raw = value.GetRawText();
        if (raw.Length <= maxChars) return raw;
        return JsonSerializer.Serialize(raw.Substring(0, maxChars) + "…(truncated)");
    }

    // For tools that return a persisted, downloadable artifact, pluck out just
    // the metadata fields the chat UI needs to render a link. Returns null for
    // every tool that doesn't fit the pattern — the caller skips the
    // `attachment` key entirely in that case so the event stays small.
    //
    // Recognized shape: { report_artifact_id, filename, content_type, format,
    // size_bytes } — matches GenerateReportHandler's output. New file-producing
    // tools just need to return the same fields to get the same affordance.
    private static string? BuildAttachmentJson(string toolName, ToolCallOutput output)
    {
        if (!string.Equals(toolName, "generate_report", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!output.Success) return null;

        try
        {
            var root = output.Result;
            if (root.ValueKind != JsonValueKind.Object) return null;

            static string? Str(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            static int? Int(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32() : null;

            var id = Str(root, "report_artifact_id");
            var filename = Str(root, "filename");
            var contentType = Str(root, "content_type");
            var format = Str(root, "format");
            var size = Int(root, "size_bytes");
            if (id is null || filename is null) return null;

            return JsonSerializer.Serialize(new
            {
                kind = "report",
                report_artifact_id = id,
                filename,
                content_type = contentType,
                format,
                size_bytes = size,
                download_url = $"/api/reports/{id}/download",
            });
        }
        catch
        {
            // Never break the stream because of an attachment parse error —
            // the agent's text output still lands for the user either way.
            return null;
        }
    }
}
