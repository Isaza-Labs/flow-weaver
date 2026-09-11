using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Interfaces;

public interface IDevicePool : IBaseService<DevicePoolResponse, CreateDevicePool, UpdateDevicePool>
{
}
