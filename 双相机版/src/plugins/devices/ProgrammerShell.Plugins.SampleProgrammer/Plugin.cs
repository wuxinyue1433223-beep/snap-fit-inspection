using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.Plugins.SampleProgrammer;

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
 yield return new SampleCameraFactory(_loggerFactory);
 }
}
