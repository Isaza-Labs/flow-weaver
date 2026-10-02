using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Controllers;

// Formal publication of the canonical workflow JSON schema.
// Unauthenticated on purpose — integrators (CI, external pipelines,
// docs sites) need to fetch the schema without credentials. The schema
// is already shipped publicly in the open source drop, so exposing it
// here doesn't leak anything.
[ApiController]
[AllowAnonymous]
[Route("api/schema")]
public class SchemaController : ControllerBase
{
    private readonly IWorkflowSchemaValidator _validator;

    public SchemaController(IWorkflowSchemaValidator validator)
    {
        _validator = validator;
    }

    // Returns the schema the currently-running backend validates against.
    // Clients that want version stability should pin via the versioned
    // endpoint below; /workflow/latest tracks whatever the binary ships.
    [HttpGet("workflow/latest")]
    [Produces("application/schema+json", "application/json")]
    public IActionResult Latest()
    {
        Response.Headers.Append("X-Schema-Version", _validator.CurrentSchemaVersion);
        return Content(_validator.RawJson, "application/schema+json");
    }

    // Explicit version pin. Today only `v1` is shipped; a future `v2`
    // will land alongside a new branch here without breaking existing
    // integrators pointing at `v1`.
    [HttpGet("workflow/{version}")]
    [Produces("application/schema+json", "application/json")]
    public IActionResult Versioned(string version)
    {
        if (!string.Equals(version, _validator.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
            return NotFound(new
            {
                error = "unknown schema version",
                available = new[] { _validator.CurrentSchemaVersion },
            });
        Response.Headers.Append("X-Schema-Version", _validator.CurrentSchemaVersion);
        return Content(_validator.RawJson, "application/schema+json");
    }
}
