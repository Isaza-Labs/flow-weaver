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
[HasPermission("device.read")]
public class DeviceController : ControllerBase
{
    private readonly IDevice _service;

    public DeviceController(IDevice service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<DeviceResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<DeviceResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("device.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DeviceResponse>> Post([FromBody] CreateDevice dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("device.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DeviceResponse>> Update(Guid id, [FromBody] UpdateDevice dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("device.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DeviceResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);
}
