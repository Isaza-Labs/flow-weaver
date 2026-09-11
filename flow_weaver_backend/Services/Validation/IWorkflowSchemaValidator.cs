using System.Text.Json;

namespace flow_weaver_backend.Services.Validation;

// Validates a workflow's nodes + edges against workflow.v1.schema.json.
// Called by WorkflowService before persisting so malformed DAGs never
// land in the DB. Downstream (Sprint 2 DagParser) assumes the shape is
// already valid and only checks semantic rules (cycles, unknown refs).
public interface IWorkflowSchemaValidator
{
    // The supported schema version. Services stamp this on every workflow
    // they accept so a future v2 can coexist without a destructive migration.
    string CurrentSchemaVersion { get; }

    // Raw JSON of the embedded schema file — exposed so the schema
    // publication endpoint (FR-007) can serve it verbatim without
    // re-reading the embedded resource itself.
    string RawJson { get; }

    WorkflowValidationResult Validate(JsonElement nodes, JsonElement edges);
}

// Warnings are non-blocking advisories: the workflow persists, but the
// service echoes them back in the response so the caller can act. Used
// today by WorkflowReferenceValidator to flag suspicious-looking DAGs
// like "this workflow talks about email but has no integration_action
// node". The agent sees them in the create response and can self-
// correct before asking the user to run.
public record WorkflowValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<SchemaViolation>? Violations = null)
{
    public static WorkflowValidationResult Ok() =>
        new(true, Array.Empty<string>(), null);

    public static WorkflowValidationResult OkWithWarnings(IReadOnlyList<string> warnings) =>
        new(true, Array.Empty<string>(), warnings);

    public static WorkflowValidationResult Invalid(IReadOnlyList<string> errors) =>
        new(false, errors, null);

    public static WorkflowValidationResult Invalid(
        IReadOnlyList<string> errors, IReadOnlyList<SchemaViolation> violations) =>
        new(false, errors, null, violations);
}

/// <summary>
/// One structural violation, addressable: the schema rule that failed and the place in the
/// submitted document where it failed, beside the message a person reads.
/// </summary>
/// <param name="Keyword">The JSON Schema keyword that rejected the value — `required`, `type`,
/// `additionalProperties`, `enum`.</param>
/// <param name="Path">A JSON Pointer into the submitted `{nodes, edges}` document, e.g.
/// `/nodes/0/snippet_id`. Empty for a violation against the document root.</param>
/// <param name="Message">The human-readable text, identical to the corresponding entry in
/// <see cref="WorkflowValidationResult.Errors"/> minus its pointer prefix.</param>
/// <remarks>
/// This exists ALONGSIDE the message list rather than replacing it. The messages reach clients
/// verbatim as the `details` array of the `400 schema_invalid` body, and retyping that array
/// would break anything already parsing it; an editor that wants to put a marker on the
/// offending node cannot get there from prose.
///
/// Only STRUCTURAL validation fills this in. A reference that does not resolve and a vendor
/// command that is invalid for a device type have no schema keyword and no document location,
/// and inventing one for them would be fabricating data to satisfy a shape — see
/// <c>WorkflowReferenceValidator</c> and <c>VendorCommandValidator</c>, which report messages
/// only and are meant to stay that way.
/// </remarks>
public record SchemaViolation(string Keyword, string Path, string Message);
