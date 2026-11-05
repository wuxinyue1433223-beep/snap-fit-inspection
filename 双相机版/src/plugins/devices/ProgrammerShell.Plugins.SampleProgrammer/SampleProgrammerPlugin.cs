using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.Plugins.SampleProgrammer;

public sealed class SampleProgrammerPlugin : IDevicePlugin
{
 private ILoggerFactory? _loggerFactory;

 public SampleProgrammerPlugin()
 {
 }

 public void Initialize(ILoggerFactory loggerFactory)
 {
 _loggerFactory = loggerFactory;
 }

 public IEnumerable<IDeviceFactory> GetDeviceFactories()
 {
 yield return new SampleProgrammerFactory(_loggerFactory);
 yield return new SampleCameraFactory(_loggerFactory);
 }
}
