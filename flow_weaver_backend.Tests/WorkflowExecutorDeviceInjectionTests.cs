using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// Covers the #6 device-context fix: per_device python_snippets get the
// current device stamped as inp['device'] so a script can read
// inp['device']['name'] directly — the shape authors reach for — instead
// of falling back to "unknown-device" (the node-devices-configs-v3 bug).
public class WorkflowExecutorDeviceInjectionTests
{
    private static readonly MethodInfo InjectDevice =
        typeof(WorkflowExecutor).GetMethod("InjectDevice",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InjectDevice not found");

    private static JsonElement Invoke(string payload, string device) =>
        (JsonElement)InjectDevice.Invoke(null, new object[]
        {
            JsonDocument.Parse(payload).RootElement,
            JsonDocument.Parse(device).RootElement,
        })!;

    [Fact]
    public void Adds_device_object_when_absent_preserving_other_keys()
    {
        var result = Invoke("""{"foo":1}""", """{"name":"node-l1","platform":"nokia_srl"}""");

        Assert.Equal("node-l1", result.GetProperty("device").GetProperty("name").GetString());
        Assert.Equal("nokia_srl", result.GetProperty("device").GetProperty("platform").GetString());
        Assert.Equal(1, result.GetProperty("foo").GetInt32());
    }

    [Fact]
    public void Author_supplied_device_is_left_untouched()
    {
        var result = Invoke("""{"device":{"name":"override"}}""", """{"name":"node-l1"}""");

        Assert.Equal("override", result.GetProperty("device").GetProperty("name").GetString());
    }
}
