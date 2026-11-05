namespace ProgrammerShell.Core.Interfaces;

public interface IDeviceManager
{
 IReadOnlyList<IDeviceFactory> Factories { get; }
 IReadOnlyList<IDevice> ActiveDevices { get; }

 Task ScanPluginsAsync(CancellationToken token = default);

 IDevice CreateDevice(string deviceType);
 IDevice CreateDevice(string deviceType, string? deviceId);
 void RegisterDevice(IDevice device);
 bool UnregisterDevice(IDevice device);

 Task ShutdownAsync(CancellationToken token = default);
}
