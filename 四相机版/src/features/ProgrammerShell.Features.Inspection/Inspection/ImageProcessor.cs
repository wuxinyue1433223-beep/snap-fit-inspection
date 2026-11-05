using OpenCvSharp;
using ProgrammerShell.Features.Inspection.PLC;

namespace ProgrammerShell.Features.Inspection.Inspection;

/// <summary>
/// 图像处理器：模板匹配检测（OK/NG），并将结果写入 PLC
/// </summary>
public sealed class ImageProcessor : IDisposable
{
    private readonly TemplateMatchDetector _templateDetector;
    private readonly PLCCommunicator? _plcCommunicator;
    private readonly int _cameraId;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public ImageProcessor(int cameraId, PLCCommunicator? plcCommunicator = null, string? configPath = null)
    {
        _cameraId = cameraId;
        _plcCommunicator = plcCommunicator;
        // If configPath points to a directory, treat it as templates root.
        // Otherwise fall back to default {solutionRoot}\templates\default.
        var templatesRoot = !string.IsNullOrWhiteSpace(configPath) && Directory.Exists(configPath)
            ? configPath
            : null;

        _templateDetector = new TemplateMatchDetector(templatesRoot);
    }

    public DetectionResult ProcessImage(string imagePath)
    {
        var result = new DetectionResult { CameraId = _cameraId, Timestamp = DateTime.UtcNow, ImagePath = imagePath };

        try
        {
            if (!CheckFileSize(imagePath))
            {
                result.Result = "NG";
                result.PLCResult = "NG";
                result.ColorDetected = "Image Not Ready";
                result.IsSuccess = false;
                return result;
            }

            using var source = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
            if (source == null || source.Empty())
            {
                result.Result = "NG";
                result.PLCResult = "NG";
                result.ColorDetected = "Image Read Failed";
                result.IsSuccess = false;
                return result;
            }

            var (isOk, annotated, total, successful) = DetectWithRetry(source);
            using (annotated)
            {
                result.IsFirstDetection = false;
                result.IsSuccess = isOk;
                result.Result = isOk ? "OK" : "NG";
                result.PLCResult = result.Result;
                result.ColorDetected = total > 0
                    ? $"Template {successful}/{total}"
                    : "No Template";

                if (string.Equals(result.ColorDetected, "No Light", StringComparison.OrdinalIgnoreCase))
                    result.ColorDetected = "Processing Error";

                SaveImage(annotated, isOk ? "ok" : "ng", result);
            }

            return result;
        }
        catch (Exception ex)
        {
            LogProcessException(ex, imagePath);
            result.Result = "NG";
            result.PLCResult = "NG";
            result.ColorDetected = "Processing Error";
            result.IsSuccess = false;
            return result;
        }
    }

    public async Task<DetectionResult> ProcessImageAsync(string imagePath)
    {
        await _processGate.WaitAsync();
        try
        {
            return ProcessImage(imagePath);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private bool CheckFileSize(string imagePath)
    {
        const int maxRetries = 15;
        const long minFileSizeBytes = 512; // 0.5KB
        long lastLength = -1;

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                var fi = new FileInfo(imagePath);
                if (!fi.Exists || fi.Length < minFileSizeBytes)
                {
                    lastLength = -1;
                    Thread.Sleep(50);
                    continue;
                }

                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fi.Length == lastLength)
                    return true;

                lastLength = fi.Length;

                Thread.Sleep(50);
            }
            catch
            {
                lastLength = -1;
                Thread.Sleep(50);
            }
        }

        return false;
    }

    private (bool IsOk, Mat AnnotatedBgr, int Total, int Successful) DetectWithRetry(Mat image)
    {
        var first = _templateDetector.Detect(_cameraId, image);
        if (first.Total > 0 && first.Successful == 0)
        {
            first.AnnotatedBgr.Dispose();
            return _templateDetector.Detect(_cameraId, image);
        }

        return first;
    }

    private void LogProcessException(Exception ex, string imagePath)
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            Console.Error.WriteLine(
                $"[ImageProcessor] ProcessImage异常: cam={_cameraId}, image={imagePath}, exception={ex}");
            Console.Error.WriteLine(
                $"[ImageProcessor] Memory: Heap={GC.GetTotalMemory(false)}, TotalAvailable={memoryInfo.TotalAvailableMemoryBytes}, HighLoadThreshold={memoryInfo.HighMemoryLoadThresholdBytes}, Fragmented={memoryInfo.FragmentedBytes}, Committed={memoryInfo.TotalCommittedBytes}, Gen0={GC.CollectionCount(0)}, Gen1={GC.CollectionCount(1)}, Gen2={GC.CollectionCount(2)}");
        }
        catch
        {
        }
    }

    private void SaveImage(Mat bgrFrame, string resultFolder, DetectionResult result)
    {
        try
        {
            var root = GetSolutionRoot();
            var camFolder = (_cameraId + 1).ToString();
            var targetFolder = Path.Combine(root, "photo", resultFolder, camFolder);
            Directory.CreateDirectory(targetFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var savePath = Path.Combine(targetFolder, $"{timestamp}.jpg");
            Cv2.ImWrite(savePath, bgrFrame);
            result.SavePath = Path.GetFullPath(savePath);

            ImageRetentionService.RegisterFolder(Path.Combine(root, "photo"));
            ImageRetentionService.CleanupRegisteredFolders(TimeSpan.FromDays(180));
        }
        catch
        {
            // ignore
        }
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    public void ResetDetectionState() { }
    public (bool IsFirstImage, string? FirstImageResult) GetDetectionState() => (true, null);

    public void Dispose()
    {
        _templateDetector.Dispose();
        _processGate.Dispose();
    }
}
