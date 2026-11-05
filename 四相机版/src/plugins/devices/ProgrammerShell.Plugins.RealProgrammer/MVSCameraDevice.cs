using Microsoft.Extensions.Logging;
using MvCameraControl;
using ProgrammerShell.Core.Interfaces;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ProgrammerShell.Plugins.RealProgrammer.Camera.MVSCamera
{
    public sealed class MVSCameraDevice : ICamera
    {
        private const int MaxPreviewDimension = 960;
        private const int MvErrorNoData = unchecked((int)0x80000007);
        private readonly ILogger _logger;
        private readonly IDeviceInfo _deviceInfo;
        private MvCameraControl.IDevice _cameraDevice = null;
        private string _saveImageBasePath;
        private string? _lastCapturedImagePath;
        private readonly SemaphoreSlim _triggerLock = new(1, 1);
        private bool _isStreamGrabbing;

        public string DeviceId { get; }
        public string Name { get; }
        public DeviceState State { get; private set; } = DeviceState.Disconnected;
        public string? LastCapturedImagePath => _lastCapturedImagePath;

        public event EventHandler<CameraFrame>? FrameReady;

        public MVSCameraDevice(ILogger logger, IDeviceInfo deviceInfo)
        {
            _logger = logger;
            _deviceInfo = deviceInfo;
            DeviceId = deviceInfo.SerialNumber;
            Name = string.IsNullOrEmpty(deviceInfo.UserDefinedName)
                ? $"{deviceInfo.TLayerType}: {deviceInfo.ManufacturerName} {deviceInfo.ModelName}"
                : deviceInfo.UserDefinedName;

            // Save triggered source frames into OS temp cache (not project photo folder).
            _saveImageBasePath = Path.Combine(Path.GetTempPath(), "ProgrammerShell", "capture-cache");
            Directory.CreateDirectory(_saveImageBasePath);
        }

        // Try to match MainWindow.GetSolutionRoot() behavior without adding a project reference.
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

        private string GetCaptureFileName()
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeDeviceId = string.IsNullOrWhiteSpace(DeviceId)
                ? "camera"
                : new string(DeviceId.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return $"{safeDeviceId}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.bmp";
        }

        public Task ConnectAsync(CancellationToken token = default)
        {
            if (State == DeviceState.Connected)
                return Task.CompletedTask;
            State = DeviceState.Connecting;

            try
            {
                
                _cameraDevice = DeviceFactory.CreateDevice(_deviceInfo);
                int ret = _cameraDevice.Open();
                if (ret == MvError.MV_OK) { _logger.LogInformation("相机打开成功：{DeviceId}", DeviceId); }
                if (ret != MvError.MV_OK)
                {
                    throw new InvalidOperationException($"相机打开失败，错误码：0x{ret:X}");
                }

                ConfigureSoftwareTriggerMode();
                _isStreamGrabbing = false;

                State = DeviceState.Connected;
                _logger.LogInformation("成功连接相机：{DeviceId} ({Name})", DeviceId, Name);
            }
            catch (Exception ex)
            {
                State = DeviceState.Disconnected;
                _logger.LogError(ex, "相机连接失败：{DeviceId}", DeviceId);
                throw;
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken token = default)
        {
            StopGrabbingAsync(token).GetAwaiter().GetResult();

            if (State == DeviceState.Disconnected)
                return Task.CompletedTask;
            if (_cameraDevice != null)
            {
                _cameraDevice.Close();
                _cameraDevice.Dispose();
                _cameraDevice = null;
            }

            State = DeviceState.Disconnected;
            _logger.LogInformation("已断开相机连接：{DeviceId}", DeviceId);
            return Task.CompletedTask;
        }

        public Task StartGrabbingAsync(CancellationToken token = default)
        {
            if (State != DeviceState.Connected)
                throw new InvalidOperationException("相机未连接");

            if (!_isStreamGrabbing)
            {
                var ret = _cameraDevice.StreamGrabber.StartGrabbing();
                if (ret != MvError.MV_OK)
                    throw new InvalidOperationException($"开始采集失败，错误码：0x{ret:X}");

                _isStreamGrabbing = true;
            }

            _logger.LogInformation("相机已进入软触发单帧模式：{DeviceId}, SavePath={SavePath}", DeviceId, _saveImageBasePath);
            return Task.CompletedTask;
        }

        public Task StopGrabbingAsync(CancellationToken token = default)
        {
            if (_cameraDevice == null)
                return Task.CompletedTask;

            if (!_isStreamGrabbing)
                return Task.CompletedTask;

            int ret = _cameraDevice.StreamGrabber.StopGrabbing();
            if (ret != MvError.MV_OK && ret != MvError.MV_E_CALLORDER)
            {
                _logger.LogError("停止采集失败，错误码：0x{Ret:X} (DeviceId: {DeviceId})", ret, DeviceId);
            }
            else
            {
                _logger.LogInformation("相机已停止采集：{DeviceId}", DeviceId);
            }

            _isStreamGrabbing = false;

            return Task.CompletedTask;
        }

        public Task SoftTriggerAsync(CancellationToken token = default)
        {
            if (_cameraDevice == null || State != DeviceState.Connected)
                throw new InvalidOperationException("相机未连接");

            _logger.LogInformation("收到软触发请求：{DeviceId}", DeviceId);

            return Task.Run(() => CaptureSingleFrameAsync(token), token);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                DisconnectAsync(CancellationToken.None).Wait();
            }
            catch { }
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            try
            {
                _cameraDevice?.Dispose();
            }
            catch { }
        }

        private async Task CaptureSingleFrameAsync(CancellationToken token)
        {
            await _triggerLock.WaitAsync(token);

            try
            {
                IFrameOut? frame = null;
                IFrameOut? frameOut = null;
                var startedTemporarily = false;

                try
                {
                    if (!_isStreamGrabbing)
                    {
                        int startRet = _cameraDevice.StreamGrabber.StartGrabbing();
                        if (startRet != MvError.MV_OK)
                            throw new InvalidOperationException($"开始采集失败，错误码：0x{startRet:X}");

                        _isStreamGrabbing = true;
                        startedTemporarily = true;
                        _logger.LogInformation("软触发前临时开启采集：{DeviceId}", DeviceId);
                    }

                    var triggerSent = TrySendSoftwareTriggerCommand();
                    if (triggerSent)
                        _logger.LogInformation("软件触发命令发送成功：{DeviceId}", DeviceId);
                    else
                        _logger.LogWarning("未找到可用的软件触发命令接口，继续尝试取流：{DeviceId}", DeviceId);

                    var ret = _cameraDevice.StreamGrabber.GetImageBuffer(1200, out frame);
                    if (ret == MvErrorNoData)
                    {
                        _logger.LogWarning("首次取图无数据：{DeviceId}, Ret=0x{Ret:X}，准备重试一次", DeviceId, ret);

                        if (triggerSent)
                            _ = TrySendSoftwareTriggerCommand();

                        ret = _cameraDevice.StreamGrabber.GetImageBuffer(1200, out frame);
                    }

                    if (ret != MvError.MV_OK || frame == null || frame.Image == null)
                        throw new InvalidOperationException($"获取单帧图像失败，错误码：0x{ret:X}");

                    _logger.LogInformation("获取到原始图像：{DeviceId}, {ImageInfo}", DeviceId, DescribeImage(frame.Image));

                    frameOut = frame;
                    if (frame.Image.PixelType.ToString().Contains("HB", StringComparison.OrdinalIgnoreCase))
                    {
                        var decodeResult = _cameraDevice.ImageDecoder.HBDecode(frame, out frameOut);
                        if (decodeResult != MvError.MV_OK)
                            throw new InvalidOperationException($"HB 图像解码失败，错误码：0x{decodeResult:X}");

                        if (frameOut?.Image is not null)
                            _logger.LogInformation("HB 图像解码完成：{DeviceId}, {ImageInfo}", DeviceId, DescribeImage(frameOut.Image));
                    }

                    SaveTriggeredImage(frameOut.Image);

                    var cameraFrame = ConvertToCameraFrame(frameOut.Image);
                    _logger.LogInformation("预览图像转换完成：{DeviceId}, PreviewWidth={Width}, PreviewHeight={Height}, BufferLength={BufferLength}", DeviceId, cameraFrame.Width, cameraFrame.Height, cameraFrame.Bgra32Pixels.Length);
                    FrameReady?.Invoke(this, cameraFrame);
                    _logger.LogInformation("软触发单帧采集完成：{DeviceId}", DeviceId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "软触发单帧采集异常：{DeviceId}", DeviceId);
                    throw;
                }
                finally
                {
                    try
                    {
                        if (frameOut != null && frame != null && !ReferenceEquals(frameOut, frame))
                            frameOut.Image.Dispose();
                    }
                    catch { }

                    try
                    {
                        if (frame != null)
                            _cameraDevice.StreamGrabber.FreeImageBuffer(frame);
                    }
                    catch { }

                    if (startedTemporarily)
                    {
                        try
                        {
                            var stopRet = _cameraDevice.StreamGrabber.StopGrabbing();
                            if (stopRet == MvError.MV_OK || stopRet == MvError.MV_E_CALLORDER)
                                _logger.LogInformation("单帧采集停止成功：{DeviceId}, Ret=0x{Ret:X}", DeviceId, stopRet);
                            else
                                _logger.LogWarning("单帧采集停止返回异常：{DeviceId}, Ret=0x{Ret:X}", DeviceId, stopRet);

                            _isStreamGrabbing = false;
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                _triggerLock.Release();
            }
        }

        private CameraFrame ConvertToCameraFrame(IImage image)
        {
            var width = (int)image.Width;
            var height = (int)image.Height;
            var (previewWidth, previewHeight) = GetPreviewSize(width, height);

            var bgra = ConvertToBgra32Preview(image, width, height, previewWidth, previewHeight);
            return new CameraFrame(previewWidth, previewHeight, bgra);
        }

        private static (int Width, int Height) GetPreviewSize(int width, int height)
        {
            if (width <= MaxPreviewDimension && height <= MaxPreviewDimension)
                return (width, height);

            var scale = Math.Min((double)MaxPreviewDimension / width, (double)MaxPreviewDimension / height);
            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private byte[] ConvertToBgra32Preview(IImage image, int width, int height, int previewWidth, int previewHeight)
        {
            try
            {
                var pixelData = image.PixelData;
                if (pixelData is null || pixelData.Length == 0)
                    return new byte[previewWidth * previewHeight * 4];

                var pt = image.PixelType.ToString();
                var pinned = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
                try
                {
                    if (pixelData.Length < width * height)
                    {
                        _logger.LogWarning("Pixel buffer too small: PixelType={PixelType}, Width={Width}, Height={Height}, PixelDataLen={Len}", pt, width, height, pixelData.Length);
                    }

                    if (pt.Contains("Mono", StringComparison.OrdinalIgnoreCase) || pt.Contains("Mono8", StringComparison.OrdinalIgnoreCase))
                    {
                        using var mono = Mat.FromPixelData(height, width, MatType.CV_8UC1, pinned.AddrOfPinnedObject());
                        using var previewMono = ResizeIfNeeded(mono, previewWidth, previewHeight);
                        using var bgra = new Mat();
                        Cv2.CvtColor(previewMono, bgra, ColorConversionCodes.GRAY2BGRA);
                        return ToTightBgra(bgra, previewWidth, previewHeight);
                    }

                    if (pt.Contains("BGR", StringComparison.OrdinalIgnoreCase) || pt.Contains("Bgr8", StringComparison.OrdinalIgnoreCase))
                    {
                        using var bgr = Mat.FromPixelData(height, width, MatType.CV_8UC3, pinned.AddrOfPinnedObject());
                        using var previewBgr = ResizeIfNeeded(bgr, previewWidth, previewHeight);
                        using var bgra = new Mat();
                        Cv2.CvtColor(previewBgr, bgra, ColorConversionCodes.BGR2BGRA);
                        return ToTightBgra(bgra, previewWidth, previewHeight);
                    }

                    if (pt.Contains("Bayer", StringComparison.OrdinalIgnoreCase))
                    {
                        using var bayer = Mat.FromPixelData(height, width, MatType.CV_8UC1, pinned.AddrOfPinnedObject());
                        using var bgr = new Mat();

                        var code = pt switch
                        {
                            var s when s.Contains("RG", StringComparison.OrdinalIgnoreCase) => ColorConversionCodes.BayerRG2BGR,
                            var s when s.Contains("BG", StringComparison.OrdinalIgnoreCase) => ColorConversionCodes.BayerBG2BGR,
                            var s when s.Contains("GR", StringComparison.OrdinalIgnoreCase) => ColorConversionCodes.BayerGR2BGR,
                            var s when s.Contains("GB", StringComparison.OrdinalIgnoreCase) => ColorConversionCodes.BayerGB2BGR,
                            _ => ColorConversionCodes.BayerBG2BGR
                        };

                        Cv2.CvtColor(bayer, bgr, code);
                        using var previewBgr = ResizeIfNeeded(bgr, previewWidth, previewHeight);
                        using var bgra = new Mat();
                        Cv2.CvtColor(previewBgr, bgra, ColorConversionCodes.BGR2BGRA);
                        return ToTightBgra(bgra, previewWidth, previewHeight);
                    }

                    var expectedBgra = width * height * 4;
                    if (pixelData.Length >= expectedBgra)
                    {
                        using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, pinned.AddrOfPinnedObject());
                        using var previewBgra = ResizeIfNeeded(bgra, previewWidth, previewHeight);
                        return ToTightBgra(previewBgra, previewWidth, previewHeight);
                    }
                }
                finally
                {
                    if (pinned.IsAllocated)
                        pinned.Free();
                }
            }
            catch (OutOfMemoryException ex)
            {
                _logger.LogWarning(ex, "预览图像转换内存不足，已回退为空白预览 (DeviceId: {DeviceId})", DeviceId);
            }
            catch
            {
            }

            return new byte[previewWidth * previewHeight * 4];
        }

        private static Mat ResizeIfNeeded(Mat source, int width, int height)
        {
            if (source.Width == width && source.Height == height)
                return source.Clone();

            var resized = new Mat();
            Cv2.Resize(source, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        private static byte[] ToTightBgra(Mat bgra, int width, int height)
        {
            var tight = new byte[width * height * 4];

            if (bgra.Empty() || bgra.Width != width || bgra.Height != height)
                return tight;

            if (bgra.Type() != MatType.CV_8UC4)
                return tight;

            if (bgra.Step() == width * 4)
            {
                Marshal.Copy(bgra.Data, tight, 0, tight.Length);
                return tight;
            }

            // Handle row padding (stride > width*4)
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var srcRowPtr = bgra.Data + (int)(y * bgra.Step());
                Marshal.Copy(srcRowPtr, tight, y * rowBytes, rowBytes);
            }

            return tight;
        }

        private void SaveTriggeredImage(IImage image)
        {
            try
            {
                ImageFormatInfo formatInfo = new ImageFormatInfo();
                formatInfo.FormatType = ImageFormatType.Bmp;

                string fileName = GetCaptureFileName();
                string fullSavePath = Path.Combine(_saveImageBasePath, fileName);

                _logger.LogInformation("开始保存软触发图片：{DeviceId}, Path={Path}, {ImageInfo}", DeviceId, fullSavePath, DescribeImage(image));

                int ret = _cameraDevice.ImageSaver.SaveImageToFile(
                    fullSavePath,
                    image,
                    formatInfo,
                    CFAMethod.Equilibrated
                );

                if (ret == MvError.MV_OK)
                {
                    File.SetLastWriteTimeUtc(fullSavePath, DateTime.UtcNow);
                    _lastCapturedImagePath = fullSavePath;
                    var fileInfo = new FileInfo(fullSavePath);
                    _logger.LogInformation("软触发图片保存成功：{Path} (DeviceId: {DeviceId}, Size={Size}, LastWriteUtc={LastWriteUtc:O})", fullSavePath, DeviceId, fileInfo.Exists ? fileInfo.Length : -1, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue);
                }
                else
                {
                    _logger.LogError("图片保存失败，错误码：0x{Ret:X} (DeviceId: {DeviceId}, Path: {Path})", ret, DeviceId, fullSavePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "图片保存异常 (DeviceId: {DeviceId})", DeviceId);
            }
        }

        public void SetSaveImagePath(string newPath)
        {
            if (!string.IsNullOrEmpty(newPath))
            {
                _saveImageBasePath = newPath;
                if (!Directory.Exists(_saveImageBasePath))
                {
                    Directory.CreateDirectory(_saveImageBasePath);
                }
                _logger.LogInformation("相机图片保存路径已更新：{Path} (DeviceId: {DeviceId})", newPath, DeviceId);
            }
        }

        private static string DescribeImage(IImage image)
        {
            var pixelLength = image.PixelData?.Length ?? 0;
            return $"Width={image.Width}, Height={image.Height}, PixelType={image.PixelType}, PixelDataLength={pixelLength}";
        }

        private void ConfigureSoftwareTriggerMode()
        {
            var parameters = _cameraDevice.Parameters;

            var okAcquisitionMode = SetEnumParameter(parameters, "AcquisitionMode", "Continuous", 2);
            var okTriggerSelector = SetEnumParameter(parameters, "TriggerSelector", "FrameStart", 0);
            var okTriggerSource = SetEnumParameter(parameters, "TriggerSource", "Software", 7);
            var okTriggerMode = SetEnumParameter(parameters, "TriggerMode", "On", 1);

            _logger.LogInformation(
                "软触发模式参数配置结果：{DeviceId}, AcquisitionMode={AcquisitionMode}, TriggerSelector={TriggerSelector}, TriggerSource={TriggerSource}, TriggerMode={TriggerMode}",
                DeviceId,
                okAcquisitionMode,
                okTriggerSelector,
                okTriggerSource,
                okTriggerMode);

            if (!okTriggerMode || !okTriggerSource)
                _logger.LogWarning("部分软触发参数未设置成功（SDK 接口不兼容），将继续以当前相机参数运行：{DeviceId}", DeviceId);
        }

        private bool TrySendSoftwareTriggerCommand()
        {
            var parameters = _cameraDevice.Parameters;
            var type = parameters.GetType();

            var setCommand = type.GetMethod("SetCommandValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (setCommand is not null)
            {
                setCommand.Invoke(parameters, new object[] { "TriggerSoftware" });
                return true;
            }

            var setCommandByName = type.GetMethod("SetCommandValueByString", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (setCommandByName is not null)
            {
                setCommandByName.Invoke(parameters, new object[] { "TriggerSoftware" });
                return true;
            }

            var executeCommand = type.GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (executeCommand is not null)
            {
                executeCommand.Invoke(parameters, new object[] { "TriggerSoftware" });
                return true;
            }

            var runCommand = type.GetMethod("RunCommand", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (runCommand is not null)
            {
                runCommand.Invoke(parameters, new object[] { "TriggerSoftware" });
                return true;
            }

            return false;
        }

        private bool SetEnumParameter(object parameters, string key, string symbolicValue, long numericValue)
        {
            var type = parameters.GetType();

            var setByName = type.GetMethod("SetEnumValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);
            if (setByName is not null)
            {
                setByName.Invoke(parameters, new object[] { key, symbolicValue });
                _logger.LogInformation("参数设置：{DeviceId}, {Key}={Value}(symbolic)", DeviceId, key, symbolicValue);
                return true;
            }

            var setByName2 = type.GetMethod("SetEnumValueByString", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);
            if (setByName2 is not null)
            {
                setByName2.Invoke(parameters, new object[] { key, symbolicValue });
                _logger.LogInformation("参数设置：{DeviceId}, {Key}={Value}(symbolic2)", DeviceId, key, symbolicValue);
                return true;
            }

            var setByLong = type.GetMethod("SetEnumValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(long) }, null);
            if (setByLong is not null)
            {
                setByLong.Invoke(parameters, new object[] { key, numericValue });
                _logger.LogInformation("参数设置：{DeviceId}, {Key}={Value}(numeric)", DeviceId, key, numericValue);
                return true;
            }

            var setByInt = type.GetMethod("SetEnumValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int) }, null);
            if (setByInt is not null)
            {
                setByInt.Invoke(parameters, new object[] { key, (int)numericValue });
                _logger.LogInformation("参数设置：{DeviceId}, {Key}={Value}(numeric)", DeviceId, key, numericValue);
                return true;
            }

            var setByUInt = type.GetMethod("SetEnumValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(uint) }, null);
            if (setByUInt is not null)
            {
                setByUInt.Invoke(parameters, new object[] { key, (uint)Math.Max(0, numericValue) });
                _logger.LogInformation("参数设置：{DeviceId}, {Key}={Value}(numeric)", DeviceId, key, numericValue);
                return true;
            }

            _logger.LogWarning("参数设置失败（接口不兼容）：{DeviceId}, Key={Key}, symbolic={SymbolicValue}, numeric={NumericValue}", DeviceId, key, symbolicValue, numericValue);
            return false;
        }
    }
}