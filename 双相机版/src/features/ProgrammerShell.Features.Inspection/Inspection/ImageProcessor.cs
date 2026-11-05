using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static readonly SemaphoreSlim _globalProcessGate = new(1, 1);
    private readonly ILogger<ImageProcessor> _logger;
    private static long _processedCount;
    private bool _disposed;

    public ImageProcessor(int cameraId, PLCCommunicator? plcCommunicator = null, string? configPath = null, ILogger<ImageProcessor>? logger = null)
    {
        _cameraId = cameraId;
        _plcCommunicator = plcCommunicator;
        _logger = logger ?? NullLogger<ImageProcessor>.Instance;
        // If configPath points to a directory, treat it as templates root.
        // Otherwise fall back to default {solutionRoot}\templates\default.
        var templatesRoot = !string.IsNullOrWhiteSpace(configPath) && Directory.Exists(configPath)
            ? configPath
            : null;

        _templateDetector = new TemplateMatchDetector(templatesRoot);
    }

    public DetectionResult ProcessImage(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new DetectionResult { CameraId = _cameraId, Timestamp = DateTime.UtcNow, ImagePath = imagePath };
        var startedAt = Stopwatch.StartNew();
        var sequence = Interlocked.Increment(ref _processedCount);

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

                SaveImage(annotated, isOk ? "OK" : "NG", result);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图像处理异常：CameraId={CameraId}, ImagePath={ImagePath}, ElapsedMs={ElapsedMs}, Memory={Memory}", _cameraId, imagePath, startedAt.ElapsedMilliseconds, GetMemorySnapshot());
            result.Result = "NG";
            result.PLCResult = "NG";
            result.ColorDetected = "Processing Error";
            result.IsSuccess = false;
            return result;
        }
        finally
        {
            if (sequence % 10 == 0)
            {
                _logger.LogInformation("图像处理心跳：CameraId={CameraId}, Sequence={Sequence}, ElapsedMs={ElapsedMs}, Memory={Memory}", _cameraId, sequence, startedAt.ElapsedMilliseconds, GetMemorySnapshot());
            }
        }
    }

    public async Task<DetectionResult> ProcessImageAsync(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _globalProcessGate.WaitAsync();
        await _processGate.WaitAsync();
        try
        {
            return ProcessImage(imagePath);
        }
        finally
        {
            _processGate.Release();
            _globalProcessGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _templateDetector.Dispose();
        _processGate.Dispose();
        _disposed = true;
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

    private (bool IsOk, Mat AnnotatedImage, int Total, int Successful) DetectWithRetry(Mat image)
    {
        var first = _templateDetector.Detect(_cameraId, image);
        if (first.Total > 0 && first.Successful == 0)
        {
            first.AnnotatedImage.Dispose();
            return _templateDetector.Detect(_cameraId, image);
        }

        return first;
    }

    private void SaveImage(Mat frame, string resultFolder, DetectionResult result)
    {
        try
        {
            var root = GetSolutionRoot();
            var camFolder = $"Cam{_cameraId + 1}";
            var targetFolder = Path.Combine(root, "photo", camFolder, resultFolder);
            Directory.CreateDirectory(targetFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var savePath = Path.Combine(targetFolder, $"{timestamp}.jpg");

            var h = frame.Rows;
            var w = frame.Cols;
            var baseScale = Math.Max(w, h) / 2000.0;
            var fontScale = Math.Max(0.6, 1.0 * baseScale);
            var thickness = Math.Max(2, (int)(3 * baseScale));
            var displayText = $"{result.Result} - {result.ColorDetected} ({result.PLCResult})";
            var overlayColor = frame.Channels() == 1
                ? Scalar.White
                : string.Equals(result.Result, "OK", StringComparison.OrdinalIgnoreCase) ? Scalar.LimeGreen : Scalar.Red;
            Cv2.PutText(
                frame,
                displayText,
                new Point(10, Math.Max(40, (int)(50 * baseScale))),
                HersheyFonts.HersheySimplex,
                fontScale,
                overlayColor,
                thickness);

            Cv2.ImWrite(savePath, frame);
            result.SavePath = Path.GetFullPath(savePath);

            ImageRetentionService.RegisterFolder(Path.Combine(root, "photo", camFolder));
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

    private static string GetMemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var managed = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
        var workingSet = process.WorkingSet64 / (1024 * 1024);
        var privateBytes = process.PrivateMemorySize64 / (1024 * 1024);
        return $"managed={managed}MB, workingSet={workingSet}MB, private={privateBytes}MB";
    }

    public void ResetDetectionState() { }
    public (bool IsFirstImage, string? FirstImageResult) GetDetectionState() => (true, null);
}
