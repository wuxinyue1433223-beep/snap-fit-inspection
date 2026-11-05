using System.IO;
using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.App.Services;

public sealed class DeviceManager : IDeviceManager
{
 private readonly ILogger<DeviceManager> _logger;
 private readonly ILoggerFactory _loggerFactory;
 private readonly PluginLoader _pluginLoader;

 private readonly List<IDeviceFactory> _factories = new();
 private readonly List<IDevice> _activeDevices = new();
 private readonly object _gate = new();

 public DeviceManager(ILogger<DeviceManager> logger, ILoggerFactory loggerFactory, PluginLoader pluginLoader)
 {
 _logger = logger;
 _loggerFactory = loggerFactory;
 _pluginLoader = pluginLoader;
 }

 public IReadOnlyList<IDeviceFactory> Factories
 {
 get { lock (_gate) return _factories.ToList(); }
 }

 public IReadOnlyList<IDevice> ActiveDevices
 {
 get { lock (_gate) return _activeDevices.ToList(); }
 }

 public async Task ScanPluginsAsync(CancellationToken token = default)
 {
 var roots = new List<string>
 {
 Path.Combine(AppContext.BaseDirectory, "app", "plugins"),
 };

 roots.AddRange(FindPluginRootsByWalkingUp(AppContext.BaseDirectory));

 var loadedFactories = new List<IDeviceFactory>();
 foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
 {
 try
 {
 var f = await _pluginLoader.LoadDeviceFactoriesAsync(root, token);
 if (f.Count >0)
 {
 loadedFactories.AddRange(f);
 _logger.LogInformation("Loaded {Count} factories from {Root}", f.Count, root);
 }
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "Plugin load failed for root {Root}", root);
 }
 }

 // Ensure a built-in virtual camera option is always available.
 if (!loadedFactories.Any(f => string.Equals(f.DeviceType, "virtual-camera", StringComparison.OrdinalIgnoreCase)))
 {
 loadedFactories.Add(new LocalVirtualCameraFactory(_loggerFactory));
 }

 lock (_gate)
 {
 _factories.Clear();
 _factories.AddRange(loadedFactories);
 }

 foreach (var factory in loadedFactories)
 {
 try
 {
 _ = await factory.DiscoverAsync(token);
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "DiscoverAsync failed for factory {DeviceType}", factory.DeviceType);
 }
 }

 _logger.LogInformation("ScanPlugins completed. Factories: {FactoryCount}", loadedFactories.Count);
 }

 private static IEnumerable<string> FindPluginRootsByWalkingUp(string startDir)
 {
 var roots = new List<string>();
 var dir = new DirectoryInfo(startDir);
 for (var i =0; i <6 && dir is not null; i++, dir = dir.Parent)
 {
 var candidate1 = Path.Combine(dir.FullName, "src", "plugins", "devices");
 var candidate2 = Path.Combine(dir.FullName, "plugins", "devices");
 if (Directory.Exists(candidate1)) roots.Add(candidate1);
 if (Directory.Exists(candidate2)) roots.Add(candidate2);
 }
 return roots;
 }

 public IDevice CreateDevice(string deviceType)
 {
 return CreateDevice(deviceType, null);
 }

 public IDevice CreateDevice(string deviceType, string? deviceId)
 {
 IDeviceFactory? factory;
 lock (_gate)
 {
 factory = _factories.FirstOrDefault(f => string.Equals(f.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase));
 }

 if (factory is null)
 throw new InvalidOperationException($"Factory not found for deviceType: {deviceType}");

 var logger = _loggerFactory.CreateLogger($"Device:{factory.DeviceType}");
 return factory is IDeviceFactoryWithSelection selectable
 ? selectable.CreateDevice(logger, deviceId)
 : factory.CreateDevice(logger);
 }

 public void RegisterDevice(IDevice device)
 {
 lock (_gate)
 {
 _activeDevices.Add(device);
 }
 }

 public bool UnregisterDevice(IDevice device)
 {
 lock (_gate)
 {
 return _activeDevices.Remove(device);
 }
 }

 public async Task ShutdownAsync(CancellationToken token = default)
 {
 List<IDevice> devices;
 lock (_gate)
 {
 devices = _activeDevices.ToList();
 _activeDevices.Clear();
 }

 foreach (var device in devices)
 {
 try
 {
 await device.DisconnectAsync(token);
 }
 catch { }

 try
 {
 await device.DisposeAsync();
 }
 catch { }

 try
 {
 device.Dispose();
 }
 catch { }
 }

 _logger.LogInformation("DeviceManager shutdown complete.");
 }
}
