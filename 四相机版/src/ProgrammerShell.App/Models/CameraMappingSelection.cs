namespace ProgrammerShell.App;

// NOTE: FactoryIndex is not stable across runs because the factory list can change order
// (e.g., plugin load success/failure). Persisting FactoryDeviceType makes the mapping stable.
public sealed record CameraMappingSelection(int FactoryIndex, string? DeviceId, string? FactoryDeviceType = null);
