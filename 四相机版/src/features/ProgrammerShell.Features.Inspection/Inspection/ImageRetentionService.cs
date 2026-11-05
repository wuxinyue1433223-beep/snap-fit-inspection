using System.Collections.Concurrent;

namespace ProgrammerShell.Features.Inspection.Inspection;

public static class ImageRetentionService
{
    private static readonly ConcurrentDictionary<string, byte> _folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cleanupGate = new();
    private static DateOnly? _lastCleanupDay;

    public static void RegisterFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        _folders.TryAdd(folder, 0);
    }

    public static void CleanupRegisteredFolders(TimeSpan maxAge)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        lock (_cleanupGate)
        {
            if (_lastCleanupDay == today)
                return;

            _lastCleanupDay = today;
        }

        foreach (var folder in _folders.Keys)
        {
            try
            {
                CleanupFolder(folder, maxAge);
            }
            catch
            {
                // ignore
            }
        }
    }

    public static void CleanupFolder(string folder, TimeSpan maxAge)
    {
        if (!Directory.Exists(folder))
            return;

        var cutoff = DateTime.Now - maxAge;

        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                var t = File.GetCreationTime(file);
                if (t == DateTime.MinValue)
                    t = File.GetLastWriteTime(file);

                if (t < cutoff)
                    File.Delete(file);
            }
            catch
            {
                // ignore
            }
        }

        // delete empty directories bottom-up
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, false);
                }
                catch { }
            }
        }
        catch
        {
        }
    }
}
