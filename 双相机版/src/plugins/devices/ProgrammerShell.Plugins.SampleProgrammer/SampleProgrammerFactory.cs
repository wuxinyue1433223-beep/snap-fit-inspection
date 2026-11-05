using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;
using System.IO;

namespace ProgrammerShell.Plugins.SampleProgrammer;

public sealed class SampleProgrammerFactory : IDeviceFactory
{
 private readonly ILoggerFactory? _loggerFactory;

 public SampleProgrammerFactory(ILoggerFactory? loggerFactory)
 {
 _loggerFactory = loggerFactory;
 }

 public string DeviceType => "sample-programmer";
 public string DisplayName => "Sample";
 public bool IsCloneable => true;

 public Task<bool> DiscoverAsync(CancellationToken token = default)
 {
 return Task.FromResult(true);
 }

 public IDevice CreateDevice(ILogger logger)
 {
 return new SimulatedProgrammer(logger);
 }
}

public sealed class SampleCameraFactory : IDeviceFactoryWithSelection
{
 private readonly ILoggerFactory? _loggerFactory;

 public SampleCameraFactory(ILoggerFactory? loggerFactory)
 {
 _loggerFactory = loggerFactory;
 }

 public string DeviceType => "sample-camera";
 public string DisplayName => "Virtual Camera";
 public bool IsCloneable => true;

 public Task<bool> DiscoverAsync(CancellationToken token = default)
 {
 return Task.FromResult(true);
 }

 public IReadOnlyList<DeviceDescriptor> GetAvailableDevices()
 {
 return new List<DeviceDescriptor>
 {
  new("VirtualCam-1", "Virtual Camera 1"),
  new("VirtualCam-2", "Virtual Camera 2"),
  new("VirtualCam-3", "Virtual Camera 3"),
  new("VirtualCam-4", "Virtual Camera 4")
 };
 }

 public IDevice CreateDevice(ILogger logger)
 {
  return new SimulatedCamera(logger, "VirtualCam-1", null);
 }

 public IDevice CreateDevice(ILogger logger, string? deviceId)
 {
  // Use the selected device id (e.g. VirtualCam-1..4 or a folder path) to infer cam index.
  var name = string.IsNullOrWhiteSpace(deviceId) ? "VirtualCam-1" : Path.GetFileName(deviceId.TrimEnd('\\', '/'));
  return new SimulatedCamera(logger, name, deviceId);
 }
}
