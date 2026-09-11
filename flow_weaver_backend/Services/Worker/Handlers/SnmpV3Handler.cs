namespace flow_weaver_backend.Services.Worker.Handlers;

// Stub — SNMPv3 requires a USM-capable SNMP client; the project has no
// such dependency today. Preferred library is `Lextm.SharpSnmpLib`, which
// supports GET/GETNEXT/WALK/SET with authPriv (SHA/AES-256). Until that is
// added, this stub returns a clear error so workflows referencing type
// `snmp_v3` fail actionably instead of "no handler registered".
public sealed class SnmpV3Handler : ISnippetHandler
{
    public string Type => "snmp_v3";

    private readonly ILogger<SnmpV3Handler> _logger;

    public SnmpV3Handler(ILogger<SnmpV3Handler> logger)
    {
        _logger = logger;
    }

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        _logger.LogDebug(
            "worker.snmp_v3.start step_run_id={StepRunId} device_id={DeviceId}",
            request.StepRunId, request.DeviceId);

        _logger.LogError(
            "worker.snmp_v3.failed step_run_id={StepRunId} device_id={DeviceId} reason={Reason}",
            request.StepRunId, request.DeviceId, "not_implemented");

        return Task.FromResult(new SnippetResult
        {
            // Registered but not implemented: it refuses, so nothing happened.
            Change = StepChange.Unchanged,
            Success = false,
            Error = "snmp_v3 handler is not implemented yet — add Lextm.SharpSnmpLib and wire " +
                    "USM (authPriv) using the device credential",
        });
    }
}
