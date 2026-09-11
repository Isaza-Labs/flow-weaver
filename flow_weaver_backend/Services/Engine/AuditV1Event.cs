using System.Text.Json.Serialization;

namespace flow_weaver_backend.Services.Engine;

/// <summary>
/// One executed mutation, in the shared `audit.v1` shape.
/// </summary>
/// <remarks>
/// Ported member-for-member from Nashira's record of the same name, because the shape IS the
/// contract: a conformance vector compares the event object WHOLE, so an engine that dropped
/// a member or renamed one fails rather than quietly forking an interop format.
///
/// The wire names sit on the record rather than being applied by whoever serialises it. An
/// event spelled one way in the audit row and another way on a wire would be two formats
/// wearing one name, and only one of them would be the contract's.
///
/// Two members deserve their own note:
///
///   <c>op</c>        is the node's snippet id, not a verb. It answers "what ran", and the
///                    node id beside it answers "where".
///   <c>tenant_id</c>  is part of the schema even though this product has no tenants, and is
///                    emitted as the nil GUID. Dropping it here would silently fork the
///                    format for every reader that expects it.
///
/// Emitted once per node that CHANGED something — not once per step that ran. That is the
/// whole reason the change signal had to exist first.
/// </remarks>
public sealed record AuditV1Event(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("schema_hash")] string SchemaHash,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("idempotency")] string Idempotency,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("result")] string Result);
