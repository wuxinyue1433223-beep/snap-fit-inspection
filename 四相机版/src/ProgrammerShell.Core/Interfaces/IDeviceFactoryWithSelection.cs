using Microsoft.Extensions.Logging;

namespace ProgrammerShell.Core.Interfaces;

public sealed record DeviceDescriptor(string DeviceId, string DisplayName);

public interface IDeviceFactoryWithSelection : IDeviceFactory
{
    IReadOnlyList<DeviceDescriptor> GetAvailableDevices();
    IDevice CreateDevice(ILogger logger, string? deviceId);
}
