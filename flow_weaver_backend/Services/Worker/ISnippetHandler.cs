namespace flow_weaver_backend.Services.Worker;

// Whether a step's effect can be safely re-run or undone. Promotion to
// `production` flags workflows that contain steps with non-reversible
// effects so reviewers see them before they approve.
//
//   Idempotent          — safe to re-run with the same input. Default
//                         for read-only handlers (ping, transform,
//                         get-style rest_call, snmp_v3 reads, ssh show).
//   RequiresCompensation — has side effects in external systems but a
//                         compensating action exists (DELETE for create,
//                         transactional REST flow). The workflow author
//                         must wire the compensation in a failure edge.
//   NonReversible       — has external side effects with no automatic
//                         compensation (sending an email, pushing a
//                         git commit, running ansible against a device
//                         without rollback hooks). Promotion warns on
//                         these and PromotionService.RollbackAsync
//                         refuses to roll back a workflow whose graph
//                         contains them.
public enum IdempotencyKind
{
    Idempotent = 0,
    RequiresCompensation = 1,
    NonReversible = 2,
}

// Contract for a step handler. Each snippet type (ping, rest_call,
// transform, integration_action, ssh, …) gets its own implementation
// registered in DI as ISnippetHandler. The worker resolves the handler
// by matching Snippet.Type against ISnippetHandler.Type.
//
// Implementations are Scoped (one per job dispatch) so they can inject
// a fresh DbContext if needed (integration_action reads integration rows).
public interface ISnippetHandler
{
    // Must match the Snippet.Type value exactly (case-insensitive
    // lookup in the worker). E.g. "ping", "rest_call", "transform".
    string Type { get; }

    // Default idempotency for steps using this handler. The author can
    // override it per-snippet via Snippet.Idempotency, which the
    // editor surfaces as the "this step can be retried/rolled back"
    // toggle. The handler default is the floor: a handler that ships
    // marked NonReversible cannot be downgraded by the snippet config.
    IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct);
}
