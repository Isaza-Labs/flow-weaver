# schema/

`workflow.v1.schema.json` — the structural JSON Schema for a `workflow.v1` document, reified from
FlowWeaver's workflow model. It is normative for the `schema` vector family (structural validity)
and is available to every implementation's adapter.

FlowWeaver's own validator (`flow_weaver_backend/Services/Validation/WorkflowSchemaValidator.cs`)
loads the copy embedded in the backend, `flow_weaver_backend/Data/Schemas/workflow.v1.schema.json`.
Its conformance adapter answers the `schema` family through that production validator, so the
vectors check the path real workflows take rather than this file in isolation.
