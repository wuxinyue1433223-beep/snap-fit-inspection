using ProgrammerShell.Features.Inspection.PLC;

namespace ProgrammerShell.Features.Inspection.Inspection;

/// <summary>
/// 文件夹监控器，遍历 cam_0..cam_3 并触发处理
/// </summary>
public sealed class FolderMonitor
{
    private readonly string _folderPath;
    private readonly Dictionary<int, ImageProcessor> _processors;
    private readonly Dictionary<string, DateTime> _fileModificationTimes = new();
    private readonly object _fileTimesLock = new();
    private readonly string _processingSnapshotFolder;
    private FileSystemWatcher? _watcher;

    public event EventHandler<DetectionResult>? DetectionCompleted;

    public FolderMonitor(string folderPath, string? configFilePath = null, PLCCommunicator? plc = null)
    {
        _folderPath = folderPath;
        _processingSnapshotFolder = Path.Combine(_folderPath, ".processing");
        Directory.CreateDirectory(_processingSnapshotFolder);
        _processors = new Dictionary<int, ImageProcessor>();
        for (var i = 0; i < 4; i++)
        {
            _processors[i] = new ImageProcessor(i, plc, configFilePath);
        }
    }

    public void Start()
    {
        if (!Directory.Exists(_folderPath))
            return;

        _watcher = new FileSystemWatcher(_folderPath)
        {
            Filter = "*.bmp",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var targetImages = new[] { "cam_0.bmp", "cam_1.bmp", "cam_2.bmp", "cam_3.bmp" };
            foreach (var img in targetImages)
            {
                var path = Path.Combine(_folderPath, img);
                if (!File.Exists(path))
                    continue;

                try
                {
                    var mtime = File.GetLastWriteTime(path);
                    lock (_fileTimesLock)
                    {
                        if (_fileModificationTimes.TryGetValue(path, out var prev) && prev == mtime)
                            continue;

                        _fileModificationTimes[path] = mtime;
                    }

                    var camId = GetCameraIdFromFilename(img);
                    Task.Run(async () =>
                    {
                        var snapshotPath = await CreateProcessingSnapshotAsync(path, camId);
                        try
                        {
                            var res = await _processors[camId].ProcessImageAsync(snapshotPath);
                            DetectionCompleted?.Invoke(this, res);
                        }
                        finally
                        {
                            TryDeleteFile(snapshotPath);
                        }
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_fileTimesLock)
        {
            if (_fileModificationTimes.ContainsKey(e.FullPath))
                _fileModificationTimes.Remove(e.FullPath);
        }
    }

    private static int GetCameraIdFromFilename(string filename)
    {
        var parts = filename.Split('_', '.');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var id))
            return id;
        return 0;
    }

    public async Task<DetectionResult> ProcessImageManually(string imagePath)
    {
        var filename = Path.GetFileName(imagePath);
        var camId = GetCameraIdFromFilename(filename ?? string.Empty);
        if (_processors.TryGetValue(camId, out var proc))
        {
            var snapshotPath = await CreateProcessingSnapshotAsync(imagePath, camId);
            try
            {
                var res = await proc.ProcessImageAsync(snapshotPath);
                DetectionCompleted?.Invoke(this, res);
                return res;
            }
            finally
            {
                TryDeleteFile(snapshotPath);
            }
        }

        return new DetectionResult { CameraId = camId, Result = "NG", PLCResult = "NG", ColorDetected = "Processing Error", IsSuccess = false };
    }

    public void ResetCamera(int cameraId)
    {
        if (_processors.TryGetValue(cameraId, out var p))
            p.ResetDetectionState();
    }

    public void ResetAllCameras()
    {
        foreach (var p in _processors.Values)
            p.ResetDetectionState();
    }

    private async Task<string> CreateProcessingSnapshotAsync(string sourcePath, int camId)
    {
        var snapshotPath = Path.Combine(
            _processingSnapshotFolder,
            $"cam_{camId}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.bmp");

        const int maxRetries = 8;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Copy(sourcePath, snapshotPath, overwrite: false);
                return snapshotPath;
            }
            catch when (i < maxRetries - 1)
            {
                await Task.Delay(25);
            }
        }

        throw new IOException($"Failed to snapshot image for processing: {sourcePath}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
