using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;
using System.IO;
using OpenCvSharp;

namespace ProgrammerShell.Plugins.SampleProgrammer;

public sealed class SimulatedCamera : ICamera
{
 private const int MaxPreviewDimension = 1280;
 private readonly ILogger _logger;
 private readonly string? _imageRoot;
 private readonly int? _fixedCamIndex;
 // This directory is where we will SAVE the triggered frame,
 // matching MVSCameraDevice behavior ({solutionRoot}\photo\temp-trigger).
 private readonly string _saveImageBasePath;

 private bool _hasLoggedSource;
 private DeviceState _state = DeviceState.Disconnected;
 private CancellationTokenSource? _grabCts;
 private Task? _grabTask;
 private long _frameIndex;
 private string? _lastCapturedImagePath;

 public SimulatedCamera(ILogger logger, string name, string? deviceId)
 {
 _logger = logger;
 DeviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;
 Name = name;

 _imageRoot = GetImageRoot(deviceId);
 _fixedCamIndex = TryParseCameraIndex(name) ?? TryParseCameraIndex(deviceId);

 // Initialize save path for detection usage
 _saveImageBasePath = Path.Combine(GetSolutionRoot(), "photo", "temp-trigger");
 Directory.CreateDirectory(_saveImageBasePath);
 }

 // Reuse the same logic to find solution root
 private static string GetSolutionRoot()
 {
 var dir = new DirectoryInfo(AppContext.BaseDirectory);
 while (dir is not null)
 {
 if (Directory.Exists(Path.Combine(dir.FullName, "src")))
 {
 return dir.FullName;
 }
 dir = dir.Parent;
 }
 return AppContext.BaseDirectory;
 }

 private static string GetImageRoot(string? deviceId)
 {
 // Fixed-file mode root folder:
 // 1) deviceId used as a directory
 // 2) env var PROGRAMMERSHELL_SIMCAM_ROOT
 // 3) default D:\data\photo
 if (!string.IsNullOrWhiteSpace(deviceId) && Directory.Exists(deviceId))
 return deviceId;

 var fromEnv = Environment.GetEnvironmentVariable("PROGRAMMERSHELL_SIMCAM_ROOT");
 if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
 return fromEnv;

 return @"D:\data\photo";
 }

 private static int? TryParseCameraIndex(string name)
 {
 // Expect patterns like "Cam1", "Camera 1", "1号相机" etc.
 // We map to zero-based cam index used by filename cam_{index}.bmp.
 if (string.IsNullOrWhiteSpace(name))
 return null;

 foreach (var ch in name)
 {
 if (ch >= '1' && ch <= '4')
 return (ch - '1');
 }

 return null;
 }

 public string DeviceId { get; }
 public string Name { get; }
 public DeviceState State => _state;
 public string? LastCapturedImagePath => _lastCapturedImagePath;

 public event EventHandler<CameraFrame>? FrameReady;

 public async Task ConnectAsync(CancellationToken token = default)
 {
 if (_state == DeviceState.Connected) return;
 _state = DeviceState.Connecting;
 await Task.Delay(50, token);
 _state = DeviceState.Connected;
 _logger.LogInformation("Camera connected {DeviceId}", DeviceId);

 if (!_hasLoggedSource)
 {
 _hasLoggedSource = true;
 if (_fixedCamIndex is null)
 {
 _logger.LogWarning("SimulatedCamera image source not configured. Name={Name}", Name);
 }
 else
 {
 var path = Path.Combine(_imageRoot ?? @"D:\\data\\photo", $"cam_{_fixedCamIndex.Value}.bmp");
 _logger.LogInformation("SimulatedCamera source: {Path}", path);
 }
 }
 }

 public async Task DisconnectAsync(CancellationToken token = default)
 {
 await StopGrabbingAsync(token);
 if (_state == DeviceState.Disconnected) return;
 await Task.Delay(20, token);
 _state = DeviceState.Disconnected;
 _logger.LogInformation("Camera disconnected {DeviceId}", DeviceId);
 }

 public Task StartGrabbingAsync(CancellationToken token = default)
 {
 if (_state != DeviceState.Connected)
 throw new InvalidOperationException("Camera not connected");

 if (_grabTask is { IsCompleted: false })
 return Task.CompletedTask;

 _grabCts = CancellationTokenSource.CreateLinkedTokenSource(token);
 var ct = _grabCts.Token;

 _grabTask = Task.Run(async () =>
 {
 while (!ct.IsCancellationRequested)
 {
 try
 {
 RaiseFrame(GenerateFrame(isContinuous: true));
 await Task.Delay(60, ct);
 }
 catch (OperationCanceledException)
 {
 break;
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "Simulated grab loop error");
 await Task.Delay(100, ct);
 }
 }
 }, ct);

 return Task.CompletedTask;
 }

 public async Task StopGrabbingAsync(CancellationToken token = default)
 {
 if (_grabCts is null)
 return;

 try { _grabCts.Cancel(); } catch { }

 var t = _grabTask;
 _grabTask = null;
 _grabCts.Dispose();
 _grabCts = null;

 if (t is not null)
 {
 try { await t.WaitAsync(TimeSpan.FromSeconds(1), token); }
 catch { }
 }
 }

 public async Task SoftTriggerAsync(CancellationToken token = default)
 {
 if (_state != DeviceState.Connected)
 throw new InvalidOperationException("Camera not connected");

 if (_fixedCamIndex is not null)
 {
 var p = Path.Combine(_imageRoot ?? @"D:\\data\\photo", $"cam_{_fixedCamIndex.Value}.bmp");
 try
 {
 var exists = File.Exists(p);
 var mtime = exists ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
 var size = exists ? new FileInfo(p).Length : 0;
 _logger.LogInformation("SimulatedCamera soft-trigger read: Path={Path} Exists={Exists} Size={Size} MTimeUtc={MTime}", p, exists, size, mtime);
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "SimulatedCamera soft-trigger stat failed: {Path}", p);
 }
 }

 await SaveSimulatedTriggerImageAsync(token);

 var frame = GenerateFrame(isContinuous: false);
 RaiseFrame(frame);
 }

 private async Task SaveSimulatedTriggerImageAsync(CancellationToken token)
 {
 if (_fixedCamIndex is null) return;

 try
 {
 var idx = _fixedCamIndex.Value;
 var fileName = $"cam_{idx}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.bmp";
 var savePath = Path.Combine(_saveImageBasePath, fileName);
  var sourcePath = GetSourceImagePath();

  if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
  {
  await Task.Run(() =>
  {
  File.Copy(sourcePath, savePath, overwrite: false);
  File.SetLastWriteTimeUtc(savePath, DateTime.UtcNow);
  }, token);
  _lastCapturedImagePath = savePath;
  _logger.LogInformation("SimulatedCamera copied trigger image to: {Path}", savePath);
  return;
  }

  var frame = GenerateFrame(isContinuous: false);
  await Task.Run(() =>
  {
  SaveFrameToFile(frame, savePath);
  File.SetLastWriteTimeUtc(savePath, DateTime.UtcNow);
  }, token);

  _lastCapturedImagePath = savePath;

 _logger.LogInformation("SimulatedCamera saved trigger image to: {Path}", savePath);
 }
 catch (Exception ex)
 {
 _logger.LogError(ex, "SimulatedCamera failed to save trigger image");
 }
 }

 private void RaiseFrame(CameraFrame frame)
 {
 FrameReady?.Invoke(this, frame);
 }

 private CameraFrame GenerateFrame(bool isContinuous)
 {
 var fileFrame = TryLoadFrameFromSource(isContinuous);
 if (fileFrame is not null)
 return fileFrame;

 const int w =640;
 const int h =480;
 const int bpp =4;

 var pixels = new byte[w * h * bpp];
 var idx = Interlocked.Increment(ref _frameIndex);

 for (var y =0; y < h; y++)
 {
 var row = y * w * bpp;
 for (var x =0; x < w; x++)
 {
 var o = row + x * bpp;
 var r = (byte)((x + idx) &0xFF);
 var g = (byte)((y + (idx *2)) &0xFF);
 var b = (byte)(((x ^ y) + (idx *3)) &0xFF);

 if ((x + idx) %80 <3) { r =255; g =255; b =255; }

 pixels[o +0] = b;
 pixels[o +1] = g;
 pixels[o +2] = r;
 pixels[o +3] =255;
 }
 }

 return new CameraFrame(w, h, pixels);
 }

 private CameraFrame? TryLoadFrameFromSource(bool isContinuous)
 {
 try
 {
 if (_fixedCamIndex is null)
 return null;

  var path = GetSourceImagePath();
 if (!File.Exists(path))
 return null;

  // Always try to load when the file exists. We intentionally do not use mtime caching
  // because some producers overwrite files without updating timestamps.
   Mat? source = null;
  for (var attempt = 0; attempt < 5; attempt++)
  {
   source?.Dispose();
   source = Cv2.ImRead(path, ImreadModes.Unchanged);
   if (source is not null && !source.Empty())
  break;
  Thread.Sleep(20);
  }

   if (source is null || source.Empty())
  {
   source?.Dispose();
  _logger.LogWarning("OpenCV failed to load image: {Path}", path);
  return null;
  }

  using var previewSource = ResizeToPreview(source);
  source.Dispose();
  using var bgra = new Mat();
  switch (previewSource.Channels())
  {
  case 1:
  Cv2.CvtColor(previewSource, bgra, ColorConversionCodes.GRAY2BGRA);
  break;
  case 3:
  Cv2.CvtColor(previewSource, bgra, ColorConversionCodes.BGR2BGRA);
  break;
  case 4:
  previewSource.CopyTo(bgra);
  break;
  default:
  return null;
  }

 var w = bgra.Width;
 var h = bgra.Height;
 var pixels = new byte[w * h * 4];
 System.Runtime.InteropServices.Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
 return new CameraFrame(w, h, pixels);
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "Failed to load simulated camera image from root {Root}", _imageRoot);
 return null;
 }
 }

 private string GetSourceImagePath()
 {
 return Path.Combine(_imageRoot ?? @"D:\data\photo", $"cam_{_fixedCamIndex!.Value}.bmp");
 }

 private static Mat ResizeToPreview(Mat source)
 {
 var (width, height) = GetPreviewSize(source.Width, source.Height);
 if (width == source.Width && height == source.Height)
 return source.Clone();

 var resized = new Mat();
 Cv2.Resize(source, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
 return resized;
 }

 private static (int Width, int Height) GetPreviewSize(int width, int height)
 {
 if (width <= MaxPreviewDimension && height <= MaxPreviewDimension)
 return (width, height);

 var scale = Math.Min((double)MaxPreviewDimension / width, (double)MaxPreviewDimension / height);
 return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
 }

 private static void SaveFrameToFile(CameraFrame frame, string savePath)
 {
 using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
 System.Runtime.InteropServices.Marshal.Copy(frame.Bgra32Pixels, 0, mat.Data, frame.Bgra32Pixels.Length);
 using var bgr = new Mat();
 Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
 Cv2.ImWrite(savePath, bgr);
 }

 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
 public void Dispose()
 {
 try { _grabCts?.Cancel(); } catch { }
 _grabCts?.Dispose();
 }
}
