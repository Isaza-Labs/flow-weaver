namespace flow_weaver_backend.Services.Worker.Handlers;

// Stub — NETCONF over SSH (RFC 6241) is planned but not implemented. The
// real handler needs an XML-aware NETCONF client: either a library like
// `Netconf.NET` or a hand-rolled SSH-subsystem client on top of SSH.NET
// (already in the project) that frames messages with `]]>]]>` and handles
// <hello>/<rpc>/<rpc-reply>/<notification>.
//
// This stub stays registered so workflows that reference type `netconf`
// fail with a clear, actionable message instead of "no handler registered".
public sealed class NetconfHandler : ISnippetHandler
{
    public string Type => "netconf";
    // Netconf operations modify device config; commit-confirmed flows
    // require an explicit confirm/discard edge.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly ILogger<NetconfHandler> _logger;

    public NetconfHandler(ILogger<NetconfHandler> logger)
    {
        _logger = logger;
    }

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        _logger.LogDebug(
            "worker.netconf.start step_run_id={StepRunId} device_id={DeviceId}",
            request.StepRunId, request.DeviceId);

        const string error = "netconf handler is not implemented yet — add a NETCONF client dependency " +
                             "(e.g. Netconf.NET) or build one on SSH.NET's SSH subsystem channel";

        _logger.LogError(
            "worker.netconf.failed step_run_id={StepRunId} device_id={DeviceId} reason={Reason}",
            request.StepRunId, request.DeviceId, "not_implemented");

        return Task.FromResult(new SnippetResult
        {
                // Registered but not implemented: it refuses, so nothing happened.
            Change = StepChange.Unchanged,
            Success = false,
            Error = error,
        });
    }
}
