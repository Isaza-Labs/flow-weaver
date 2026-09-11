using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("vendorcommand.read")]
public class VendorCommandController : ControllerBase
{
    private readonly IVendorCommand _service;

    public VendorCommandController(IVendorCommand service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<VendorCommandResponse>>> Get(
        [FromQuery] string? device_type = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
        => _service.GetAsync(device_type, limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<VendorCommandResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("vendorcommand.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<VendorCommandResponse>> Post([FromBody] CreateVendorCommand dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("vendorcommand.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<VendorCommandResponse>> Update(
        Guid id, [FromBody] UpdateVendorCommand dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("vendorcommand.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<VendorCommandResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);
}
