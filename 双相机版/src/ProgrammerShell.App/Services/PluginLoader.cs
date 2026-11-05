using System.IO;
using System;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.App.Services;

public sealed class PluginLoader
{
 private readonly ILogger<PluginLoader> _logger;
 private readonly ILoggerFactory _loggerFactory;

 public PluginLoader(ILogger<PluginLoader> logger, ILoggerFactory loggerFactory)
 {
 _logger = logger;
 _loggerFactory = loggerFactory;
 }

 public async Task<IReadOnlyList<IDeviceFactory>> LoadDeviceFactoriesAsync(string pluginsRoot, CancellationToken token = default)
 {
 if (!Directory.Exists(pluginsRoot))
 {
 _logger.LogInformation("Plugins root not found: {PluginsRoot}", pluginsRoot);
 return Array.Empty<IDeviceFactory>();
 }

 var manifests = Directory.EnumerateFiles(pluginsRoot, "plugin.json", SearchOption.AllDirectories)
 .Where(IsPluginManifestCandidate)
 .ToList();
 var factories = new List<IDeviceFactory>();

 foreach (var manifestPath in manifests)
 {
 token.ThrowIfCancellationRequested();

  // Safety: only load manifests under the provided pluginsRoot.
  // This avoids accidentally loading manifests from the source tree or build output twice,
  // which can lead to mixed AssemblyLoadContext/Core type identity issues.
  if (!Path.GetFullPath(manifestPath).StartsWith(Path.GetFullPath(pluginsRoot), StringComparison.OrdinalIgnoreCase))
  {
   continue;
  }

 try
 {
 // prevent a bad plugin from blocking startup
 using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
 timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
 var ct = timeoutCts.Token;

 var json = await File.ReadAllTextAsync(manifestPath, ct);
 var manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
 if (manifest is null)
 {
 _logger.LogWarning("Invalid plugin manifest: {ManifestPath}", manifestPath);
 continue;
 }

 var pluginDir = Path.GetDirectoryName(manifestPath)!;
 var entryAssemblyPath = Path.Combine(pluginDir, manifest.EntryAssembly);
 if (!File.Exists(entryAssemblyPath))
 {
 entryAssemblyPath = TryResolveEntryAssemblyFromBuildOutput(pluginDir, manifest.EntryAssembly);
 }

 if (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath))
 {
 _logger.LogWarning("Plugin entry assembly not found: {EntryAssemblyPath}", Path.Combine(pluginDir, manifest.EntryAssembly));
 continue;
 }

 var loadContext = new PluginLoadContext(entryAssemblyPath);
 var asm = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
 var entryType = asm.GetType(manifest.EntryType, throwOnError: false);
 if (entryType is null)
 {
 _logger.LogWarning("Entry type not found: {EntryType}", manifest.EntryType);
 continue;
 }

  object? pluginObj;
  try
  {
  pluginObj = Activator.CreateInstance(entryType);
  }
  catch (Exception ex)
  {
  _logger.LogError(ex, "Failed to create plugin entry instance: {EntryType}", manifest.EntryType);
  continue;
  }

  if (pluginObj is null)
  {
  _logger.LogWarning("Plugin entry instance is null: {EntryType}", manifest.EntryType);
  continue;
  }

  // In some cases the plugin may be loaded into a different AssemblyLoadContext and
  // the IDevicePlugin type identity differs. Support it via reflection by interface name.
  var pluginType = pluginObj.GetType();
  var implementsByIdentity = pluginObj is IDevicePlugin;
  var implementsByName = pluginType.GetInterfaces().Any(i => string.Equals(i.FullName, typeof(IDevicePlugin).FullName, StringComparison.Ordinal));

  if (!implementsByIdentity && !implementsByName)
  {
  _logger.LogWarning(
  "Entry type does not implement IDevicePlugin: {EntryType}. CreatedType={CreatedType}, Interfaces=[{Interfaces}]",
  manifest.EntryType,
  pluginType.FullName,
  string.Join(", ", pluginType.GetInterfaces().Select(i => i.FullName)));
  continue;
  }

  List<IDeviceFactory> pluginFactories;
  try
  {
  // Initialize(loggerFactory)
  var init = pluginType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, new[] { typeof(ILoggerFactory) });
  init?.Invoke(pluginObj, new object?[] { _loggerFactory });

  // GetDeviceFactories()
  var getFactories = pluginType.GetMethod("GetDeviceFactories", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
  var factoriesObj = getFactories?.Invoke(pluginObj, Array.Empty<object?>());
  if (factoriesObj is IEnumerable<IDeviceFactory> typed)
  {
  pluginFactories = typed.ToList();
  }
  else if (factoriesObj is System.Collections.IEnumerable any)
  {
  // When Core is loaded into plugin context, items won't be castable to host IDeviceFactory.
  var items = any.Cast<object>().ToList();
  pluginFactories = items.OfType<IDeviceFactory>().ToList();

  if (pluginFactories.Count == 0 && items.Count > 0)
  {
  _logger.LogWarning(
  "Plugin {PluginId} returned factories that are not assignable to host IDeviceFactory. ItemTypes=[{ItemTypes}]",
  manifest.PluginId,
  string.Join(", ", items.Select(x => x.GetType().AssemblyQualifiedName)));
  }
  }
  else
  {
  pluginFactories = new List<IDeviceFactory>();
  }
  }
  catch (Exception ex)
  {
  _logger.LogError(ex, "Failed to initialize plugin or get factories: {EntryType}", manifest.EntryType);
  continue;
  }

 foreach (var f in pluginFactories)
 {
 if (!factories.Any(x => string.Equals(x.DeviceType, f.DeviceType, StringComparison.OrdinalIgnoreCase)))
 factories.Add(f);
 }

 _logger.LogInformation("Loaded plugin {PluginId} with {FactoryCount} factories", manifest.PluginId, pluginFactories.Count);
 }
 catch (OperationCanceledException)
 {
 _logger.LogWarning("Plugin load timed out: {ManifestPath}", manifestPath);
 }
 catch (Exception ex)
 {
 _logger.LogError(ex, "Failed to load plugin from manifest {ManifestPath}", manifestPath);
 }
 }

 return factories;
 }

 private static bool IsPluginManifestCandidate(string manifestPath)
 {
 var parts = Path.GetFullPath(manifestPath)
 .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

 return !parts.Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
  || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
 }

 private string? TryResolveEntryAssemblyFromBuildOutput(string pluginDir, string entryAssembly)
 {
 try
 {
 var binDir = Path.Combine(pluginDir, "bin");
 if (!Directory.Exists(binDir))
 return null;

 var matches = Directory.EnumerateFiles(binDir, entryAssembly, SearchOption.AllDirectories).ToList();
 if (matches.Count == 0)
 return null;

 // Prefer the newest build output.
 return matches.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "Failed to resolve entry assembly from build output for {PluginDir}", pluginDir);
 return null;
 }
 }
}
