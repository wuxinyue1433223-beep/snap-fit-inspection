namespace ProgrammerShell.App.Services;

internal sealed class PluginManifest
{
 public required string PluginId { get; init; }
 public required string EntryAssembly { get; init; }
 public required string EntryType { get; init; }
}
