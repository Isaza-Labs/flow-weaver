using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Interfaces;

public interface IDevice : IBaseService<DeviceResponse, CreateDevice, UpdateDevice>
{
}
