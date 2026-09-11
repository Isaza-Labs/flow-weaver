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
[HasPermission("devicepool.read")]
public class DevicePoolController : ControllerBase
{
    private readonly IDevicePool _service;

    public DevicePoolController(IDevicePool service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<DevicePoolResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<DevicePoolResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("devicepool.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DevicePoolResponse>> Post([FromBody] CreateDevicePool dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("devicepool.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DevicePoolResponse>> Update(Guid id, [FromBody] UpdateDevicePool dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("devicepool.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<DevicePoolResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);
}
