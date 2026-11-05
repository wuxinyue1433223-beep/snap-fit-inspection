using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;
using ProgrammerShell.Plugins.RealProgrammer.Camera.MVSCamera;

namespace ProgrammerShell.Plugins.RealProgrammer;

public sealed class Plugin : IDevicePlugin
{
    private ILoggerFactory? _loggerFactory;

    public Plugin()
    {
    }

    public void Initialize(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IEnumerable<IDeviceFactory> GetDeviceFactories()
    {
        yield return new Factory(_loggerFactory);
    }
}
