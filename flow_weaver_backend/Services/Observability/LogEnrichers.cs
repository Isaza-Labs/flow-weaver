using Serilog.Core;
using Serilog.Events;

namespace flow_weaver_backend.Services.Observability;

// Pretty-print enrichers used by the text console sink. Both are no-ops
// for the JSON sink because the full {SourceContext} and user fields
// are already available there as separate properties.

// Collapses a long SourceContext like
//   "flow_weaver_backend.Services.Ai.OpenAIChatClient"
// into "OpenAIChatClient" so text log lines stay scannable.
public sealed class ShortContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var raw)) return;
        if (raw is not ScalarValue { Value: string full } || string.IsNullOrEmpty(full)) return;

        var idx = full.LastIndexOf('.');
        var shortName = idx >= 0 && idx < full.Length - 1 ? full[(idx + 1)..] : full;
        logEvent.AddOrUpdateProperty(factory.CreateProperty("ShortContext", shortName + " "));
    }
}

// Surfaces the caller pushed by CorrelationMiddleware as a compact tag in the
// log line, e.g. "[jdoe]". Empty string when there's no authenticated caller
// (health checks, bootstrap, workers).
public sealed class CallerTagEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var username = ReadScalar(logEvent, "username");
        var tag = string.IsNullOrEmpty(username) ? string.Empty : $"[{username}] ";
        logEvent.AddOrUpdateProperty(factory.CreateProperty("CallerTag", tag));
    }

    private static string? ReadScalar(LogEvent ev, string name)
        => ev.Properties.TryGetValue(name, out var v) && v is ScalarValue sv
            ? sv.Value?.ToString()
            : null;
}
