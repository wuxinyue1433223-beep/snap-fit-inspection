using Microsoft.Extensions.Logging;

namespace ProgrammerShell.Core.Interfaces;

public interface IDevicePlugin
{
 void Initialize(ILoggerFactory loggerFactory);
 IEnumerable<IDeviceFactory> GetDeviceFactories();
}
