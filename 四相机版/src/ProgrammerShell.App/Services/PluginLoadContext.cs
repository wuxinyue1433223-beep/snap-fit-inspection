using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace ProgrammerShell.App.Services;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
 private readonly AssemblyDependencyResolver _resolver;
 private readonly string _pluginDir;

 public PluginLoadContext(string pluginMainAssemblyPath)
 : base(isCollectible: false)
 {
 _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
 _pluginDir = Path.GetDirectoryName(pluginMainAssemblyPath) ?? AppContext.BaseDirectory;
 }

 protected override Assembly? Load(AssemblyName assemblyName)
 {
  if (TryGetDefaultAssembly(assemblyName.Name) is { } sharedAssembly)
  return sharedAssembly;

  var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
  if (assemblyPath is null)
  {
  var localPath = Path.Combine(_pluginDir, $"{assemblyName.Name}.dll");
  assemblyPath = File.Exists(localPath) ? localPath : null;
  }

  if (assemblyPath is null)
  return null;

  if (ShouldLoadInDefaultContext(assemblyName.Name))
  return LoadIntoDefaultContext(assemblyName.Name!, assemblyPath);

  return LoadFromAssemblyPath(assemblyPath);
 }

 protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
 {
 var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
 if (path is not null)
 return LoadUnmanagedDllFromPath(path);

 var localPath = Path.Combine(_pluginDir, unmanagedDllName);
 if (File.Exists(localPath))
 return LoadUnmanagedDllFromPath(localPath);

 return IntPtr.Zero;
 }

  private static Assembly? TryGetDefaultAssembly(string? assemblyName)
  {
  if (!ShouldShareWithDefaultContext(assemblyName))
  return null;

  return AssemblyLoadContext.Default.Assemblies
   .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
  }

  private static bool ShouldShareWithDefaultContext(string? assemblyName)
  {
  if (string.IsNullOrWhiteSpace(assemblyName))
  return false;

  return string.Equals(assemblyName, "ProgrammerShell.Core", StringComparison.OrdinalIgnoreCase)
   || string.Equals(assemblyName, "Microsoft.Extensions.Logging.Abstractions", StringComparison.OrdinalIgnoreCase)
   || string.Equals(assemblyName, "Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase)
   || assemblyName.StartsWith("OpenCvSharp", StringComparison.OrdinalIgnoreCase);
  }

  private static bool ShouldLoadInDefaultContext(string? assemblyName)
  {
  return false;
  }

  private static Assembly LoadIntoDefaultContext(string assemblyName, string assemblyPath)
  {
  var alreadyLoaded = AssemblyLoadContext.Default.Assemblies
   .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
  if (alreadyLoaded is not null)
  return alreadyLoaded;

  return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
  }
}
