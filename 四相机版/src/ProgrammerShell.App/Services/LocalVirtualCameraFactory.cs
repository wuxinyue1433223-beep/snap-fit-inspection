using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.App.Services;

public sealed class LocalVirtualCameraFactory : IDeviceFactoryWithSelection
{
 private readonly ILoggerFactory? _loggerFactory;

 public LocalVirtualCameraFactory(ILoggerFactory? loggerFactory)
 {
 _loggerFactory = loggerFactory;
 }

 public string DeviceType => "virtual-camera";
 public string DisplayName => "Virtual Camera";
 public bool IsCloneable => true;

 public Task<bool> DiscoverAsync(CancellationToken token = default)
 {
 return Task.FromResult(true);
 }

 public IReadOnlyList<DeviceDescriptor> GetAvailableDevices()
 {
 return new List<DeviceDescriptor>
 {
 new("VirtualCam-1", "Virtual Camera")
 };
 }

 public IDevice CreateDevice(ILogger logger)
 {
 return new LocalVirtualCamera(logger, "VirtualCam", null);
 }

 public IDevice CreateDevice(ILogger logger, string? deviceId)
 {
 return new LocalVirtualCamera(logger, "VirtualCam", deviceId);
 }
}

public sealed class LocalVirtualCamera : ICamera
{
 private readonly ILogger _logger;
 private DeviceState _state = DeviceState.Disconnected;
 private CancellationTokenSource? _grabCts;
 private Task? _grabTask;
 private long _frameIndex;

 public LocalVirtualCamera(ILogger logger, string name, string? deviceId)
 {
 _logger = logger;
 DeviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;
 Name = name;
 }

 public string DeviceId { get; }
 public string Name { get; }
 public DeviceState State => _state;
 public string? LastCapturedImagePath => null;

 public event EventHandler<CameraFrame>? FrameReady;

 public async Task ConnectAsync(CancellationToken token = default)
 {
 if (_state == DeviceState.Connected) return;
 _state = DeviceState.Connecting;
 await Task.Delay(30, token);
 _state = DeviceState.Connected;
 _logger.LogInformation("Virtual camera connected {DeviceId}", DeviceId);
 }

 public async Task DisconnectAsync(CancellationToken token = default)
 {
 await StopGrabbingAsync(token);
 if (_state == DeviceState.Disconnected) return;
 await Task.Delay(20, token);
 _state = DeviceState.Disconnected;
 _logger.LogInformation("Virtual camera disconnected {DeviceId}", DeviceId);
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
 FrameReady?.Invoke(this, GenerateFrame());
 await Task.Delay(60, ct);
 }
 catch (OperationCanceledException)
 {
 break;
 }
 catch (Exception ex)
 {
 _logger.LogWarning(ex, "Virtual camera grab loop error");
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

 public Task SoftTriggerAsync(CancellationToken token = default)
 {
 if (_state != DeviceState.Connected)
 throw new InvalidOperationException("Camera not connected");

 FrameReady?.Invoke(this, GenerateFrame());
 return Task.CompletedTask;
 }

 private CameraFrame GenerateFrame()
 {
 const int w =640;
 const int h =480;
 const int bpp =4;

 var pixels = new byte[w * h * bpp];
 var idx = Interlocked.Increment(ref _frameIndex);

 var squareSize =80;
 var sx = (int)(idx % (w - squareSize));
 var sy = (int)((idx *2) % (h - squareSize));
 var r0 = (byte)(50 + (idx *5 % 205));
 var g0 = (byte)(50 + (idx *3 % 205));
 var b0 = (byte)(50 + (idx *7 % 205));

 for (var y =0; y < h; y++)
 {
 var row = y * w * bpp;
 for (var x =0; x < w; x++)
 {
 var o = row + x * bpp;
 var r = (byte)(x * 255 / w);
 var g = (byte)(y * 255 / h);
 var b = (byte)((x + y + idx) &0x3F);

 if (x >= sx && x < sx + squareSize && y >= sy && y < sy + squareSize)
 {
 r = r0;
 g = g0;
 b = b0;
 }

 pixels[o +0] = b;
 pixels[o +1] = g;
 pixels[o +2] = r;
 pixels[o +3] =255;
 }
 }

 return new CameraFrame(w, h, pixels);
 }

 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
 public void Dispose()
 {
 try { _grabCts?.Cancel(); } catch { }
 _grabCts?.Dispose();
 }
}
