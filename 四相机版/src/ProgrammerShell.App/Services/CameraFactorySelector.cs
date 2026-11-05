using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.App.Services;

public static class CameraFactorySelector
{
 public static IReadOnlyList<IDeviceFactory> GetCameraFactories(IDeviceManager deviceManager)
 {
        static int Rank(IDeviceFactory f)
        {
            var dt = f.DeviceType ?? string.Empty;
            if (dt.Contains("real", StringComparison.OrdinalIgnoreCase)) return 0;
            if (dt.Contains("mvs", StringComparison.OrdinalIgnoreCase)) return 0;
            if (dt.Contains("sample", StringComparison.OrdinalIgnoreCase)) return 10;
            if (dt.Contains("virtual", StringComparison.OrdinalIgnoreCase)) return 10;
            return 100;
        }

        return deviceManager.Factories
            .Where(f => f.DeviceType.Contains("camera", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.DeviceType, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.DisplayName).First())
            .OrderBy(Rank)
            .ThenBy(f => f.DeviceType)
            .ThenBy(f => f.DisplayName)
            .ToList();
 }
}
