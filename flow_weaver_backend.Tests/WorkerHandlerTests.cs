using System.Net;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The two network-facing worker handlers that a unit test can actually drive:
// rest_call (scripted HttpClient) and ping (loopback only).
//
// What matters for both is the refusal path. A handler that returns a garbled
// success instead of a clear error puts a wrong value into the run's step
// output, and every downstream template then resolves against fiction — so
// every "can't do this" case has to come back as Success=false with an error a
// user can act on.
public class RestCallAndPingHandlerTests
{

    private sealed class ScriptedUrlGuard : IUrlGuard
    {
        public string? Reject { get; set; }
        public List<string> Checked { get; } = new();

        public void EnsureSafe(string url, bool allowPrivate = false)
        {
            Checked.Add(url);
            if (Reject is not null) throw new InvalidOperationException(Reject);
        }

        public void EnsureHostSafe(string host, bool allowPrivate = false)
        {
            CheckedHosts.Add(host);
            if (Reject is not null) throw new InvalidOperationException(Reject);
        }

        public List<string> CheckedHosts { get; } = new();
    }

    private static SnippetRequest Request(string input, Guid? deviceId = null, string type = "rest_call")
        => new()
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "a",
            SnippetId = Guid.NewGuid(),
            SnippetType = type,
            InputPayload = TestJson.Element(input),
            DeviceId = deviceId,
        };

    // ─── rest_call ──────────────────────────────────────────────────────

    private static RestCallHandler Rest(
        FakeHttpMessageHandler http, ScriptedUrlGuard? guard = null,
        flow_weaver_backend.Services.Ai.Secrets.ISecretResolver? secrets = null)
        => new(new FakeHttpClientFactory(http), guard ?? new ScriptedUrlGuard(),
               secrets ?? new PassThroughSecrets(),
               NullLogger<RestCallHandler>.Instance);

    private static FakeHttpMessageHandler Ok(string body = "{\"ok\":true}")
        => new(HttpStatusCode.OK, body);

    [Fact]
    public void Rest_DeclaresItsTypeAndDefaultIdempotency()
    {
        var handler = Rest(Ok());

        Assert.Equal("rest_call", handler.Type);
        // GET is idempotent but PUT/PATCH/DELETE aren't, and the verb isn't
        // known until run time — so the safe default is the pessimistic one.
        Assert.Equal(IdempotencyKind.RequiresCompensation, handler.DefaultIdempotency);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"url":""}""")]
    [InlineData("""{"url":"   "}""")]
    public async Task Rest_AMissingUrlIsRefused(string input)
    {
        var result = await Rest(Ok()).ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("input.url is required", result.Error);
    }

    // The most common agent mistake: an operationId from a spec pasted into
    // a rest_call URL. The generic Uri error is useless, so the handler
    // explains the actual fix.
    [Fact]
    public async Task Rest_AnOperationIdGetsAnActionableError()
    {
        var result = await Rest(Ok()).ExecuteAsync(
            Request("""{"url":"fw_email:/mail/send-with-attachment"}"""), default);

        Assert.False(result.Success);
        Assert.Contains("operationId", result.Error);
        Assert.Contains("integration_action", result.Error);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://files.test/x")]
    [InlineData("file:///etc/passwd")]
    public async Task Rest_OnlyAbsoluteHttpUrlsAreAccepted(string url)
    {
        var result = await Rest(Ok()).ExecuteAsync(
            Request("{\"url\":" + JsonSerializer.Serialize(url) + "}"), default);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error);
    }

    // The SSRF guard's refusal has to surface as a step failure, not an
    // unhandled exception that fails the job for the wrong reason.
    [Fact]
    public async Task Rest_TheSsrfGuardsRefusalBecomesTheStepError()
    {
        var guard = new ScriptedUrlGuard { Reject = "blocked: loopback address" };

        var result = await Rest(Ok(), guard).ExecuteAsync(
            Request("""{"url":"http://127.0.0.1/admin"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("blocked: loopback address", result.Error);
        Assert.Single(guard.Checked);
    }

    [Fact]
    public async Task Rest_ASuccessfulCallReturnsStatusBodyAndHeaders()
    {
        var http = Ok("""{"devices":[]}""");

        var result = await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/devices"}"""), default);

        Assert.True(result.Success);
        Assert.Equal(200, result.Output.GetProperty("status_code").GetInt32());
        Assert.Equal("""{"devices":[]}""", result.Output.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Object, result.Output.GetProperty("headers").ValueKind);
        Assert.Contains("200", result.Logs);
    }

    // A non-2xx is a failed step but still exposes the response so a
    // downstream node can inspect the status code.
    [Fact]
    public async Task Rest_ANon2xxFailsTheStepButKeepsTheResponse()
    {
        var result = await Rest(new FakeHttpMessageHandler(HttpStatusCode.NotFound, "nope"))
            .ExecuteAsync(Request("""{"url":"https://api.test/missing"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("HTTP 404", result.Error);
        Assert.Equal(404, result.Output.GetProperty("status_code").GetInt32());
        Assert.Equal("nope", result.Output.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Rest_TheMethodIsTakenFromTheInputAndUppercased()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x","method":"patch"}"""), default);

        Assert.Equal(HttpMethod.Patch, http.Requests[^1].Method);
    }

    [Fact]
    public async Task Rest_TheMethodDefaultsToGet()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(Request("""{"url":"https://api.test/x"}"""), default);

        Assert.Equal(HttpMethod.Get, http.Requests[^1].Method);
    }

    [Fact]
    public async Task Rest_StringHeadersAreForwarded()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x","headers":{"X-Custom":"val"}}"""), default);

        Assert.Equal("val", http.Requests[^1].Headers.GetValues("X-Custom").Single());
    }

    // A non-string header value has no sane serialisation, so it's dropped
    // rather than stringified into something the API won't understand.
    [Fact]
    public async Task Rest_NonStringHeaderValuesAreDropped()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x","headers":{"X-Num":42,"X-Ok":"yes"}}"""), default);

        Assert.False(http.Requests[^1].Headers.Contains("X-Num"));
        Assert.True(http.Requests[^1].Headers.Contains("X-Ok"));
    }

    // A JSON object body rides as raw JSON; a string body rides verbatim.
    // The request message is disposed once ExecuteAsync returns, so the body
    // and its content type are read while the request is still in flight.
    [Fact]
    public async Task Rest_AnObjectBodyIsSentAsJson()
    {
        string? contentType = null;
        var http = new FakeHttpMessageHandler(req =>
        {
            contentType = req.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x","method":"POST","body":{"name":"r1"}}"""), default);

        Assert.Equal("""{"name":"r1"}""", http.RequestBodies[^1]);
        Assert.Equal("application/json", contentType);
    }

    [Fact]
    public async Task Rest_AStringBodyIsSentVerbatim()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x","method":"POST","body":"raw text"}"""), default);

        Assert.Equal("raw text", http.RequestBodies[^1]);
    }

    [Fact]
    public async Task Rest_ARequestWithNoBodySendsNoContent()
    {
        var http = Ok();

        await Rest(http).ExecuteAsync(Request("""{"url":"https://api.test/x"}"""), default);

        Assert.Null(http.Requests[^1].Content);
    }

    // A transport failure is a step failure with the underlying reason
    // attached — not an exception escaping into the job runner.
    [Fact]
    public async Task Rest_ATransportFailureIsReportedAsAStepError()
    {
        var http = new FakeHttpMessageHandler(_ => throw new HttpRequestException("name resolution failed"));

        var result = await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x"}"""), default);

        Assert.False(result.Success);
        Assert.Contains("request failed", result.Error);
        Assert.Contains("name resolution failed", result.Error);
    }

    // A client-side timeout surfaces as TaskCanceledException with the
    // caller's token NOT cancelled — that must read as "timed out", not as
    // an operator cancel.
    [Fact]
    public async Task Rest_ATimeoutIsDistinguishedFromAnOperatorCancel()
    {
        var http = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await Rest(http).ExecuteAsync(
            Request("""{"url":"https://api.test/x"}"""), default);

        Assert.False(result.Success);
        Assert.Equal("request timed out", result.Error);
    }

    // ─── ping ───────────────────────────────────────────────────────────

    private sealed class PingFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public PingHandler Build() => new(new DeviceRepository(Db), NullLogger<PingHandler>.Instance);

        public Guid SeedDevice(string ip, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Devices.Add(new Device
            {
                DeviceId = id,
                DeviceName = "r1",
                IpAddress = ip,
                Platform = "cisco_ios",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void Ping_DeclaresItsType()
    {
        using var f = new PingFixture();

        Assert.Equal("ping", f.Build().Type);
    }

    // No host in the config and no device to fall back on: the node cannot
    // do anything, and the error says exactly what to fix.
    [Fact]
    public async Task Ping_WithNoHostAndNoDeviceIsRefused()
    {
        using var f = new PingFixture();

        var result = await f.Build().ExecuteAsync(Request("{}", type: "ping"), default);

        Assert.False(result.Success);
        Assert.Contains("provide input.host or run the step against a device", result.Error);
    }

    // The whole point of the device fallback: add a ping node, pick devices
    // in the run dialog, no templating required.
    [Fact]
    public async Task Ping_FallsBackToTheStepsDeviceAddress()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("127.0.0.1");

        var result = await f.Build().ExecuteAsync(
            Request("""{"count":1}""", deviceId: deviceId, type: "ping"), default);

        Assert.True(result.Success);
        Assert.Equal(1, result.Output.GetProperty("packets_sent").GetInt32());
    }

    // Older step_run rows only carry the target list under `_targets`; the
    // fallback keeps them working after an upgrade.
    [Fact]
    public async Task Ping_FallsBackToTheFirstTargetInTheLegacyPayload()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("127.0.0.1");

        var result = await f.Build().ExecuteAsync(
            Request("{\"count\":1,\"_targets\":[\"" + deviceId + "\"]}", type: "ping"), default);

        Assert.True(result.Success);
    }

    // A non-GUID entry in `_targets` must not derail the lookup.
    [Fact]
    public async Task Ping_IgnoresNonGuidEntriesInTheLegacyTargetList()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("127.0.0.1");

        var result = await f.Build().ExecuteAsync(
            Request("{\"count\":1,\"_targets\":[\"not-a-guid\",\"" + deviceId + "\"]}", type: "ping"),
            default);

        Assert.True(result.Success);
    }

    // NetBox-style inventories store "172.30.0.11/16". Ping would treat the
    // whole string as a hostname and DNS-resolve it — usually to the wrong
    // place — so the mask is stripped first.
    [Fact]
    public async Task Ping_StripsACidrMaskFromTheDeviceAddress()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("127.0.0.1/8");

        var result = await f.Build().ExecuteAsync(
            Request("""{"count":1}""", deviceId: deviceId, type: "ping"), default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Ping_AnUnresolvableHostIsRefusedBeforeAnyPacket()
    {
        using var f = new PingFixture();

        var result = await f.Build().ExecuteAsync(
            Request("""{"host":"this-host-does-not-exist.invalid","count":1}""", type: "ping"),
            default);

        Assert.False(result.Success);
        Assert.Contains("could not be resolved", result.Error);
    }

    // An explicit host beats the device fallback.
    [Fact]
    public async Task Ping_AnExplicitHostWinsOverTheDevice()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("this-would-not-resolve.invalid");

        var result = await f.Build().ExecuteAsync(
            Request("""{"host":"127.0.0.1","count":1}""", deviceId: deviceId, type: "ping"), default);

        Assert.True(result.Success);
    }

    // The count is clamped into [1,20] so a bad config can't turn one node
    // into a flood or a no-op.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Ping_TheCountIsClampedToAtLeastOne(int requested, int expected)
    {
        using var f = new PingFixture();

        var result = await f.Build().ExecuteAsync(
            Request("{\"host\":\"127.0.0.1\",\"count\":" + requested + "}", type: "ping"), default);

        Assert.Equal(expected, result.Output.GetProperty("packets_sent").GetInt32());
    }

    // A device row that was soft-deleted is still resolvable — the run was
    // targeted at it before the delete, and failing with "no host" would be
    // more confusing than pinging the address it had.
    [Fact]
    public async Task Ping_ASoftDeletedDeviceIsStillResolvable()
    {
        using var f = new PingFixture();
        var deviceId = f.SeedDevice("127.0.0.1", active: false);

        var result = await f.Build().ExecuteAsync(
            Request("""{"count":1}""", deviceId: deviceId, type: "ping"), default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Ping_ReportsPacketCountsAndRawOutput()
    {
        using var f = new PingFixture();

        var result = await f.Build().ExecuteAsync(
            Request("""{"host":"127.0.0.1","count":2}""", type: "ping"), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Output.GetProperty("packets_sent").GetInt32());
        Assert.Equal(2, result.Output.GetProperty("packets_received").GetInt32());
        Assert.Contains("seq=0", result.Output.GetProperty("raw_output").GetString());
        Assert.Equal("", result.Error);
    }
}


