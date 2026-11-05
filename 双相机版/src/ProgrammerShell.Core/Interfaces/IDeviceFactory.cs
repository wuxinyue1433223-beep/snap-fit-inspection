using Microsoft.Extensions.Logging;

namespace ProgrammerShell.Core.Interfaces;

public interface IDeviceFactory
{
 string DeviceType { get; }
 string DisplayName { get; }
 bool IsCloneable { get; }

 Task<bool> DiscoverAsync(CancellationToken token = default);
 IDevice CreateDevice(ILogger logger);
}
