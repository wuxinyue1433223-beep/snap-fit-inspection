using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using ProgrammerShell.App.Services;
using ProgrammerShell.Core.Interfaces;
using ProgrammerShell.Features.Inspection.Inspection;
using ProgrammerShell.Features.Inspection.PLC;
using Window = System.Windows.Window;

namespace ProgrammerShell.App;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly MainWindowViewModel _viewModel;

    private readonly Dictionary<int, CameraMappingSelection?> _cameraIndexToSelection = new()
    {
        { 1, null },
        { 2, null },
        { 3, null },
        { 4, null },
    };

    private readonly Dictionary<int, int> _cameraCaptureCounts = new()
    {
        { 1, 0 },
        { 2, 0 },
        { 3, 0 },
        { 4, 0 },
    };

    private readonly Dictionary<int, ICamera?> _camerasByIndex = new();
    private readonly Dictionary<int, bool> _isGrabbingByIndex = new();
    private readonly Dictionary<int, EventHandler<CameraFrame>> _frameHandlersByIndex = new();
    private readonly Dictionary<int, CameraFrame?> _lastFrameByIndex = new();
    private readonly Dictionary<int, bool> _isFrozenByIndex = new();
    private readonly Dictionary<int, bool> _cameraCheckStates = new();
    private readonly Dictionary<int, ProgrammerShell.Features.Inspection.Inspection.ImageProcessor> _processorsByIndex = new();
    private readonly object _plcBatchLock = new();
    private HashSet<int>? _pendingPlcTriggeredCameras;
    private readonly Dictionary<int, string?> _pendingPlcResults = new();
    private readonly Dictionary<int, string?> _pendingPlcImagePaths = new();
    private long _plcBatchSequence;
    private long? _pendingPlcBatchId;
    private PLCCommunicator? _plc;
    private bool _plcAutoInitStarted;

    private const double AutoScrollThreshold = 48;
    private const int PreviewDecodePixelWidth = 1280;
    private const bool ConfigFilesReadOnly = true;
    private bool _autoScrollRx = true;
    private bool _autoScrollTx = true;

    private sealed record PlcConnectionSettings(string? Ip, int? Port);

    public MainWindow(ILogger<MainWindow> logger, MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _logger = logger;
        _viewModel = viewModel;
        DataContext = viewModel;

        _logger.LogInformation("MainWindow started. LogPath={LogPath}", FileLoggerProvider.GetDefaultLogPath());

        // processors created here; PLC communicator is attached when user clicks PLC连接
        for (var i = 1; i <= 4; i++)
        {
            _processorsByIndex[i] = new ProgrammerShell.Features.Inspection.Inspection.ImageProcessor(i - 1, plcCommunicator: null);
        }

        for (var i = 1; i <= 4; i++)
        {
            var camIndex = i;
            _camerasByIndex[i] = null;
            _isGrabbingByIndex[i] = false;
            _frameHandlersByIndex[i] = (_, frame) => OnFrameReady(camIndex, frame);
            _lastFrameByIndex[i] = null;
            _isFrozenByIndex[i] = false;
            _cameraCheckStates[i] = false;
        }

        HookCameraPanelCommands();
        UpdateAllCameraPanelStates();

        LoadDeviceMapping();
        LoadPlcConnectionSettings();
        Loaded += OnMainWindowLoaded;

        PlcRxLogTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnPlcRxScrollChanged));
        PlcTxLogTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnPlcTxScrollChanged));
    }

    private void OnPlcRxScrollChanged(object sender, ScrollChangedEventArgs e)
        => _autoScrollRx = IsNearBottom(e);

    private void OnPlcTxScrollChanged(object sender, ScrollChangedEventArgs e)
        => _autoScrollTx = IsNearBottom(e);

    private static bool IsNearBottom(ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0)
            return true;

        // If the user is within a small threshold of the bottom, keep auto-scroll enabled.
        var distanceToBottom = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        return distanceToBottom <= AutoScrollThreshold;
    }

    private CameraPanelViewModel GetCamVm(int camIndex)
        => _viewModel.Cameras.First(c => c.CameraIndex == camIndex);

    private void HookCameraPanelCommands()
    {
        foreach (var camVm in _viewModel.Cameras)
        {
            var idx = camVm.CameraIndex;

            camVm.StartCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => StartGrabAsync(idx));
            camVm.StopCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => StopGrabAsync(idx));
            camVm.SoftTriggerCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => SoftTriggerAsync(idx));
            camVm.ResetCountCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => ResetCameraCount(idx));

            camVm.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName == nameof(CameraPanelViewModel.IsChecked))
                {
                    _cameraCheckStates[idx] = camVm.IsChecked;
                    await SetCameraConnectedAsync(idx, camVm.IsChecked);
                    UpdateCameraPanelState(idx);
                }
            };
        }
    }

    private void UpdateAllCameraPanelStates()
    {
        for (var i = 1; i <= 4; i++)
        {
            UpdateCameraPanelState(i);
        }
    }

    private void UpdateCameraPanelState(int camIndex)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        var connected = cam is not null && cam.State == DeviceState.Connected && _cameraCheckStates.GetValueOrDefault(camIndex);
        var grabbing = _isGrabbingByIndex.GetValueOrDefault(camIndex);

        var vm = GetCamVm(camIndex);
        vm.CaptureCount = _cameraCaptureCounts.GetValueOrDefault(camIndex);

        vm.StartEnabled = connected && !grabbing;
        vm.StopEnabled = connected && grabbing;
        vm.SoftTriggerEnabled = connected && grabbing;
        vm.ResetEnabled = connected && grabbing;
    }

    private void ResetCameraCount(int camIndex)
    {
        _cameraCaptureCounts[camIndex] = 0;
        ClearCameraDisplay(camIndex);
        UpdateCameraPanelState(camIndex);
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        await ApplyMappingsAsync();
        await TryStartPlcAutoConnectFromInputsAsync();
    }

    private async Task TryStartPlcAutoConnectFromInputsAsync()
    {
        if (_plcAutoInitStarted)
            return;

        _plcAutoInitStarted = true;

        var ip = PlcIpTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
            return;

        if (!int.TryParse(PlcPortTextBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            return;

        await ConfigurePlcCommunicatorAsync(ip, port, saveSettings: false, fromStartup: true);
    }

    private async Task ConfigurePlcCommunicatorAsync(string ip, int port, bool saveSettings, bool fromStartup)
    {
        PlcConnectButton.IsEnabled = false;

        try
        {
            if (fromStartup)
                AppendLine(PlcTxLogTextBox, $"PLC auto connect start: {ip}:{port}");

            var plc = await Task.Run(() => new PLCCommunicator(
                ip,
                port,
                txLog: msg => Dispatcher.Invoke(() => AppendLine(PlcTxLogTextBox, msg)),
                rxLog: msg => Dispatcher.Invoke(() => AppendLine(PlcRxLogTextBox, msg)),
                connectTimeout: TimeSpan.FromSeconds(2),
                reconnectInterval: TimeSpan.FromSeconds(1)));

            var old = _plc;
            _plc = plc;

            _plc.OnPlcTriggerBatch += triggeredCameras =>
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await HandlePlcBatchTriggerAsync(triggeredCameras);
                });
            };

            try { old?.Dispose(); } catch { }

            AppendLine(PlcTxLogTextBox, $"PLC endpoint configured: {ip}:{port}");
            if (saveSettings)
                SavePlcConnectionSettings(ip, port);
        }
        catch (Exception ex)
        {
            AppendLine(PlcTxLogTextBox, $"PLC connect error: {ex.GetBaseException().Message}");
        }
        finally
        {
            PlcConnectButton.IsEnabled = true;
        }
    }


    private async void PlcConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var ip = PlcIpTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("PLC IP 为空", "PLC");
            return;
        }

        if (!int.TryParse(PlcPortTextBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("PLC 端口不合法", "PLC");
            return;
        }

        await ConfigurePlcCommunicatorAsync(ip, port, saveSettings: true, fromStartup: false);
    }

    private static string GetMappingFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProgrammerShell");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "device-mapping.json");
    }

    private static string GetPlcSettingsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProgrammerShell");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "plc-settings.json");
    }

    private void LoadDeviceMapping()
    {
        try
        {
            var path = GetMappingFilePath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<int, CameraMappingSelection?>>(json);
            if (data is not null)
            {
                foreach (var kv in data)
                {
                    if (_cameraIndexToSelection.ContainsKey(kv.Key))
                        _cameraIndexToSelection[kv.Key] = kv.Value;
                }
                return;
            }

            var legacy = JsonSerializer.Deserialize<Dictionary<int, int?>>(json);
            if (legacy is null) return;

            foreach (var kv in legacy)
            {
                if (_cameraIndexToSelection.ContainsKey(kv.Key))
                    _cameraIndexToSelection[kv.Key] = kv.Value is null ? null : new CameraMappingSelection(kv.Value.Value, null);
            }
        }
        catch { }
    }

    private void LoadPlcConnectionSettings()
    {
        try
        {
            var path = GetPlcSettingsFilePath();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<PlcConnectionSettings>(json);
            if (!string.IsNullOrWhiteSpace(settings?.Ip))
                PlcIpTextBox.Text = settings.Ip;

            if (settings?.Port is >= 1 and <= 65535)
                PlcPortTextBox.Text = settings.Port.Value.ToString();
        }
        catch { }
    }

    private void SavePlcConnectionSettings(string ip, int port)
    {
        if (ConfigFilesReadOnly)
            return;

        try
        {
            var path = GetPlcSettingsFilePath();
            var json = JsonSerializer.Serialize(new PlcConnectionSettings(ip, port), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private void SaveDeviceMapping()
    {
        if (ConfigFilesReadOnly)
            return;

        try
        {
            var path = GetMappingFilePath();
            var json = JsonSerializer.Serialize(_cameraIndexToSelection, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }


    private async Task SetCameraConnectedAsync(int camIndex, bool connect)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        if (cam is null)
        {
            Dispatcher.Invoke(() =>
            {
                GetCamVm(camIndex).StatusText = "未配置";
                _cameraCheckStates[camIndex] = false;
                GetCamVm(camIndex).IsChecked = false;
                UpdateCameraPanelState(camIndex);
            });
            return;
        }

        try
        {
            if (connect)
            {
                await cam.ConnectAsync();
                var status = string.IsNullOrWhiteSpace(cam.DeviceId) ? "已连接" : $"已连接 ({cam.DeviceId})";
                GetCamVm(camIndex).StatusText = status;
                _isGrabbingByIndex[camIndex] = false;
                _isFrozenByIndex[camIndex] = false;
                UpdateCameraPanelState(camIndex);
            }
            else
            {
                await cam.StopGrabbingAsync();
                await cam.DisconnectAsync();
                _isGrabbingByIndex[camIndex] = false;
                _isFrozenByIndex[camIndex] = false;
                Dispatcher.Invoke(() =>
                {
                    GetCamVm(camIndex).StatusText = "未连接";
                    ClearCameraDisplay(camIndex);
                    UpdateCameraPanelState(camIndex);
                });
            }
        }
        catch (Exception ex)
        {
            GetCamVm(camIndex).StatusText = "连接失败";
            MessageBox.Show($"Camera {camIndex} connect failed: {ex.Message}", "Error");
            _cameraCheckStates[camIndex] = false;
            GetCamVm(camIndex).IsChecked = false;
            UpdateCameraPanelState(camIndex);
        }
    }

    private bool IsCameraChecked(int camIndex) => _cameraCheckStates.GetValueOrDefault(camIndex);

    private async Task StartGrabAsync(int camIndex)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        if (cam is null) return;

        if (cam.State != DeviceState.Connected || !IsCameraChecked(camIndex))
        {
            GetCamVm(camIndex).StatusText = "未连接";
            return;
        }

        try
        {
            await cam.StartGrabbingAsync();
            _isGrabbingByIndex[camIndex] = true;
            _isFrozenByIndex[camIndex] = false;
            Dispatcher.Invoke(() => UpdateCameraResultText(camIndex, string.Empty, null));
            GetCamVm(camIndex).StatusText = string.IsNullOrWhiteSpace(cam.DeviceId) ? "已连接" : $"已连接 ({cam.DeviceId})";
            UpdateCameraPanelState(camIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Start grabbing failed: {ex.Message}", "Error");
        }
    }

    private async Task StopGrabAsync(int camIndex)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        if (cam is null) return;

        try
        {
            await cam.StopGrabbingAsync();
            _isGrabbingByIndex[camIndex] = false;
            _isFrozenByIndex[camIndex] = false;
            Dispatcher.Invoke(() => ClearCameraDisplay(camIndex));
            UpdateCameraPanelState(camIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Stop grabbing failed: {ex.Message}", "Error");
        }
    }

    private async Task SoftTriggerAsync(int camIndex, long? plcBatchId = null)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        if (cam is null)
        {
            TrySendPlcBatchResult(camIndex, null, plcBatchId);
            return;
        }

        if (!_isGrabbingByIndex.GetValueOrDefault(camIndex))
        {
            TrySendPlcBatchResult(camIndex, null, plcBatchId);
            return;
        }

        if (cam.State != DeviceState.Connected || !IsCameraChecked(camIndex))
        {
            GetCamVm(camIndex).StatusText = "未连接";
            TrySendPlcBatchResult(camIndex, null, plcBatchId);
            return;
        }

        try
        {
            var triggeredAtUtc = DateTime.UtcNow;
            _isFrozenByIndex[camIndex] = false;
            _logger.LogInformation("Soft trigger start: cam={Cam}, batch={Batch}, device={DeviceId}", camIndex, plcBatchId, cam.DeviceId);

            await cam.SoftTriggerAsync();

            var capturedPath = GetValidCapturedImagePath(cam, triggeredAtUtc);
            if (capturedPath is null)
                capturedPath = await WaitForCapturedImagePathAsync(cam, triggeredAtUtc, plcBatchId is null ? 2000 : 350);
            if (capturedPath is null)
            {
                _logger.LogWarning("Soft trigger image wait timeout: cam={Cam}, batch={Batch}, device={DeviceId}", camIndex, plcBatchId, cam.DeviceId);
                TrySendPlcBatchResult(camIndex, null, plcBatchId);
                if (plcBatchId is null)
                    MessageBox.Show("No captured image available for detection.", "Warning");
                return;
            }

            try
            {
                var fileInfo = new FileInfo(capturedPath);
                _logger.LogInformation("Soft trigger image ready: cam={Cam}, path={Path}, exists={Exists}, size={Size}, lastWriteUtc={LastWriteUtc:O}", camIndex, capturedPath, fileInfo.Exists, fileInfo.Exists ? fileInfo.Length : -1, fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Soft trigger image stat failed: cam={Cam}, path={Path}", camIndex, capturedPath);
            }

            _cameraCaptureCounts[camIndex] = _cameraCaptureCounts.GetValueOrDefault(camIndex) + 1;
            UpdateCameraPanelState(camIndex);

            _isFrozenByIndex[camIndex] = true;
            Dispatcher.Invoke(() => UpdateCameraImageFromFile(camIndex, capturedPath));

            await RunDetectionAndSaveAsync(camIndex, null, triggeredAtUtc, capturedPath, plcBatchId, allowRetry: plcBatchId is null);
        }
        catch (Exception ex)
        {
            TrySendPlcBatchResult(camIndex, null, plcBatchId);
            if (plcBatchId is null)
                MessageBox.Show($"Soft trigger failed: {ex.Message}", "Error");
        }
    }

    private void OnFrameReady(int camIndex, CameraFrame frame)
    {
        _lastFrameByIndex[camIndex] = frame;

        if (_isFrozenByIndex.GetValueOrDefault(camIndex))
            return;

        if (!_cameraCheckStates.GetValueOrDefault(camIndex))
            return;

        Dispatcher.Invoke(() => UpdateCameraImage(camIndex, frame));
    }

    private void UpdateCameraImage(int camIndex, CameraFrame frame)
    {
        var bmp = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            frame.Bgra32Pixels,
            frame.Width * 4);
        bmp.Freeze();

        switch (camIndex)
        {
            case 1:
                CameraImage1.Source = bmp;
                break;
            case 2:
                CameraImage2.Source = bmp;
                break;
            case 3:
                CameraImage3.Source = bmp;
                break;
            case 4:
                CameraImage4.Source = bmp;
                break;
        }
    }

    private void ClearCameraDisplay(int camIndex)
    {
        switch (camIndex)
        {
            case 1:
                CameraImage1.Source = null;
                break;
            case 2:
                CameraImage2.Source = null;
                break;
            case 3:
                CameraImage3.Source = null;
                break;
            case 4:
                CameraImage4.Source = null;
                break;
        }

        var resultBlock = FindCameraResultTextBlock(camIndex);
        if (resultBlock is not null)
        {
            resultBlock.Text = string.Empty;
            resultBlock.Foreground = Brushes.White;
        }
    }

    private void UpdateCameraResultText(int camIndex, string text, bool? isOk)
    {
        var brush = isOk switch
        {
            true => Brushes.LimeGreen,
            false => Brushes.Red,
            _ => Brushes.White
        };

        var resultBlock = FindCameraResultTextBlock(camIndex);
        if (resultBlock is null)
            return;

        resultBlock.Text = text;
        resultBlock.Foreground = brush;
    }

    private System.Windows.Controls.TextBlock? FindCameraResultTextBlock(int camIndex)
    {
        var name = camIndex switch
        {
            1 => "Camera1ResultText",
            2 => "Camera2ResultText",
            3 => "Camera3ResultText",
            4 => "Camera4ResultText",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(name)
            ? null
            : FindName(name) as System.Windows.Controls.TextBlock;
    }

    private async Task RunDetectionAndSaveAsync(int camIndex, CameraFrame? frame, DateTime triggeredAtUtc, string? capturedPath = null, long? plcBatchId = null, bool allowRetry = true)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        string? sourceFile = null;

        try
        {
            var preferredPath = !string.IsNullOrWhiteSpace(capturedPath) ? capturedPath : cam?.LastCapturedImagePath;
            if (!string.IsNullOrWhiteSpace(preferredPath)
                && File.Exists(preferredPath)
                && File.GetLastWriteTimeUtc(preferredPath) >= triggeredAtUtc)
            {
                sourceFile = preferredPath;
            }
        }
        catch
        {
            sourceFile = null;
        }

        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            _logger.LogWarning("Detection skipped because no valid captured image is available: cam={Cam}", camIndex);
            return;
        }

        if (!_processorsByIndex.TryGetValue(camIndex, out var processor))
        {
            _logger.LogWarning("Detection processor not found: cam={Cam}", camIndex);
            return;
        }

        try
        {
            _logger.LogInformation("Detection source selected: cam={Cam}, source={Source}", camIndex, sourceFile);

            DetectionResult res;
            res = await processor.ProcessImageAsync(sourceFile);
            _logger.LogInformation("Detection first pass done: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);

            if (allowRetry && IsZeroTemplateNg(res))
            {
                _logger.LogWarning("Detection retry because zero-template NG: cam={Cam}, detail={Detail}", camIndex, res.ColorDetected);
                var retry = await processor.ProcessImageAsync(sourceFile);
                if (!IsZeroTemplateNg(retry) || string.Equals(retry.Result, "OK", StringComparison.OrdinalIgnoreCase))
                    res = retry;

                _logger.LogInformation("Detection retry result after zero-template NG: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);
            }

            if (allowRetry && IsProcessingError(res))
            {
                _logger.LogWarning("Detection retry because processing error: cam={Cam}", camIndex);
                var retry = await processor.ProcessImageAsync(sourceFile);
                if (!IsProcessingError(retry))
                    res = retry;

                _logger.LogInformation("Detection retry result after processing error: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);
            }

            if (string.Equals(res.ColorDetected, "No Light", StringComparison.OrdinalIgnoreCase))
                res.ColorDetected = "Processing Error";

            var isOk = string.Equals(res.Result, "OK", StringComparison.OrdinalIgnoreCase);
            var displayText = $"{res.Result} - {res.ColorDetected} ({res.PLCResult})";
            _logger.LogInformation("Detection done: cam={Cam}, result={Result}, plc={Plc}, detail={Detail}, save={Save}", camIndex, res.Result, res.PLCResult, res.ColorDetected, res.SavePath);
            Dispatcher.BeginInvoke(() => UpdateCameraResultText(camIndex, displayText, isOk));

            if (!string.IsNullOrWhiteSpace(res.SavePath) && File.Exists(res.SavePath))
            {
                Dispatcher.Invoke(() => UpdateCameraImageFromFile(camIndex, res.SavePath!));
            }

            TrackPlcBatchImagePath(camIndex, res.SavePath, plcBatchId);
            TrySendPlcBatchResult(camIndex, res.PLCResult, plcBatchId);
        }
        finally
        {
            TryDeleteCapturedSourceFile(sourceFile);
        }
    }

    private static string? GetValidCapturedImagePath(ICamera cam, DateTime triggeredAtUtc)
    {
        try
        {
            var p = cam.LastCapturedImagePath;
            if (!string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && File.GetLastWriteTimeUtc(p) >= triggeredAtUtc)
                return p;
        }
        catch
        {
        }

        return null;
    }

    private static async Task<string?> WaitForCapturedImagePathAsync(ICamera cam, DateTime triggeredAtUtc, int timeoutMs)
    {
        const int pollIntervalMs = 20;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                var p = cam.LastCapturedImagePath;
                if (!string.IsNullOrWhiteSpace(p)
                    && File.Exists(p)
                    && File.GetLastWriteTimeUtc(p) >= triggeredAtUtc)
                    return p;
            }
            catch
            {
            }

            await Task.Delay(pollIntervalMs);
        }

        return null;
    }

    private static void TryDeleteCapturedSourceFile(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            return;

        try
        {
            var fullPath = Path.GetFullPath(sourceFile);
            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ProgrammerShell", "capture-cache"));

            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && fullPath.IndexOf("temp-trigger", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
        }
    }

    private static bool IsZeroTemplateNg(DetectionResult res)
    {
        return string.Equals(res.Result, "NG", StringComparison.OrdinalIgnoreCase)
               && res.ColorDetected.StartsWith("Template 0/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessingError(DetectionResult res)
    {
        return string.Equals(res.Result, "NG", StringComparison.OrdinalIgnoreCase)
               && string.Equals(res.ColorDetected, "Processing Error", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandlePlcBatchTriggerAsync(IReadOnlyList<int>? triggeredByPlc)
    {
        var requested = (triggeredByPlc ?? Array.Empty<int>())
            .Where(id => id is >= 1 and <= 4)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (requested.Length == 0)
            requested = new[] { 1, 2, 3, 4 };

        long batchId;
        lock (_plcBatchLock)
        {
            _plcBatchSequence++;
            batchId = _plcBatchSequence;
            _pendingPlcBatchId = batchId;
            _pendingPlcTriggeredCameras = new HashSet<int>(requested);
            _pendingPlcResults.Clear();
            _pendingPlcImagePaths.Clear();
        }

        var tasks = new List<Task>(requested.Length);
        var triggerable = new HashSet<int>();
        foreach (var camIndex in requested)
        {
            var cam = _camerasByIndex.GetValueOrDefault(camIndex);
            var shouldTrigger = cam is not null
                                && cam.State == DeviceState.Connected
                                && IsCameraChecked(camIndex)
                                && _isGrabbingByIndex.GetValueOrDefault(camIndex);

            if (shouldTrigger)
            {
                triggerable.Add(camIndex);
            }
        }

        if (triggerable.Count == 0)
        {
            lock (_plcBatchLock)
            {
                _pendingPlcBatchId = null;
                _pendingPlcTriggeredCameras = null;
                _pendingPlcResults.Clear();
                _pendingPlcImagePaths.Clear();
            }

            _plc?.WriteBatchStatus(new Dictionary<int, string>(), requested);
            return;
        }

        foreach (var camIndex in requested)
        {
            if (triggerable.Contains(camIndex))
                tasks.Add(SoftTriggerAsync(camIndex, batchId));
            else
                TrySendPlcBatchResult(camIndex, null, batchId);
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private void TrySendPlcBatchResult(int camIndex, string? plcResult, long? plcBatchId)
    {
        if (_plc is null)
            return;

        Dictionary<int, string>? batchResults = null;
        int[]? triggeredCameras = null;
        Dictionary<int, string?>? batchImagePaths = null;
        bool moveAllToNg = false;

        lock (_plcBatchLock)
        {
            if (plcBatchId is null || _pendingPlcBatchId != plcBatchId)
                return;

            if (_pendingPlcTriggeredCameras is not { Count: > 0 } || !_pendingPlcTriggeredCameras.Contains(camIndex))
                return;

            _pendingPlcResults[camIndex] = plcResult;

            if (!_pendingPlcTriggeredCameras.All(id => _pendingPlcResults.ContainsKey(id)))
                return;

            triggeredCameras = _pendingPlcTriggeredCameras.OrderBy(id => id).ToArray();
            batchResults = triggeredCameras
                .Where(id => !string.IsNullOrWhiteSpace(_pendingPlcResults[id]))
                .ToDictionary(id => id, id => _pendingPlcResults[id]!);

            moveAllToNg = triggeredCameras.Any(id => !string.Equals(_pendingPlcResults[id], "OK", StringComparison.OrdinalIgnoreCase));
            batchImagePaths = triggeredCameras.ToDictionary(id => id, id => _pendingPlcImagePaths.GetValueOrDefault(id));

            _pendingPlcBatchId = null;
            _pendingPlcTriggeredCameras = null;
            _pendingPlcResults.Clear();
            _pendingPlcImagePaths.Clear();
        }

        if (moveAllToNg && batchImagePaths is not null)
        {
            foreach (var kv in batchImagePaths)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value) && File.Exists(kv.Value))
                {
                    Dispatcher.Invoke(() => UpdateCameraImageFromFile(kv.Key, kv.Value));
                }
            }

            foreach (var kv in batchImagePaths)
            {
                MoveResultImageToFolder(kv.Value, kv.Key, "ng");
            }
        }

        _plc.WriteBatchStatus(batchResults, triggeredCameras);
    }

    private void TrackPlcBatchImagePath(int camIndex, string? savePath, long? plcBatchId)
    {
        if (plcBatchId is null || string.IsNullOrWhiteSpace(savePath))
            return;

        lock (_plcBatchLock)
        {
            if (_pendingPlcBatchId != plcBatchId)
                return;

            if (_pendingPlcTriggeredCameras is not { Count: > 0 } || !_pendingPlcTriggeredCameras.Contains(camIndex))
                return;

            _pendingPlcImagePaths[camIndex] = savePath;
        }
    }

    private static void MoveResultImageToFolder(string? sourcePath, int camIndex, string resultFolder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        try
        {
            var root = GetSolutionRoot();
            var targetDir = Path.Combine(root, "photo", resultFolder, camIndex.ToString());
            Directory.CreateDirectory(targetDir);

            var fileName = Path.GetFileName(sourcePath);
            var targetPath = Path.Combine(targetDir, fileName);

            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                return;

            if (File.Exists(targetPath))
            {
                var ext = Path.GetExtension(fileName);
                var name = Path.GetFileNameWithoutExtension(fileName);
                targetPath = Path.Combine(targetDir, $"{name}_{Guid.NewGuid():N}{ext}");
            }

            File.Move(sourcePath, targetPath);
        }
        catch
        {
        }
    }

    private void UpdateCameraImageFromFile(int camIndex, string filePath)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = PreviewDecodePixelWidth;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }

            bmp.Freeze();

            switch (camIndex)
            {
                case 1:
                    CameraImage1.Source = bmp;
                    break;
                case 2:
                    CameraImage2.Source = bmp;
                    break;
                case 3:
                    CameraImage3.Source = bmp;
                    break;
                case 4:
                    CameraImage4.Source = bmp;
                    break;
            }
        }
        catch
        {
        }
    }

    private static async Task SaveDetectionImageAsync(int camIndex, CameraFrame frame, string displayText, bool isOk)
    {
        var root = GetSolutionRoot();
        var resultFolder = isOk ? "OK" : "NG";
        var dir = Path.Combine(root, "photo", $"Cam{camIndex}", resultFolder);
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");

        await Task.Run(() =>
        {
            using var bgra = CreateMatFromFrame(frame);
            using var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            Cv2.PutText(bgr, displayText, new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 1.0,
                isOk ? Scalar.LimeGreen : Scalar.Red, 2);
            Cv2.ImWrite(file, bgr);
        });
    }

    private static Mat CreateMatFromFrame(CameraFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.Bgra32Pixels, 0, mat.Data, frame.Bgra32Pixels.Length);
        return mat;
    }

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

    private async Task SetCameraMappingAsync(int camIndex, CameraMappingSelection? selection)
    {
        // 1. 释放旧相机资源（做空值防护）
        if (_camerasByIndex.TryGetValue(camIndex, out var existing) && existing != null)
        {
            try
            {
                existing.FrameReady -= _frameHandlersByIndex[camIndex];
                await existing.StopGrabbingAsync();
                await existing.DisconnectAsync();
                await existing.DisposeAsync();
                existing.Dispose();
            }
            catch (Exception ex)
            {
                //_logger?.LogError(ex, "释放相机{CamIndex}旧资源时异常", camIndex);
            }
            finally
            {
                _camerasByIndex[camIndex] = null;
            }
        }

        // 2. 选择项为空时重置状态
        if (selection is null)
        {
            GetCamVm(camIndex).StatusText = "未配置";
            _cameraCheckStates[camIndex] = false;
            GetCamVm(camIndex).IsChecked = false;
            ClearCameraDisplay(camIndex);
            UpdateCameraPanelState(camIndex);
            return;
        }

        // 3. 应用上下文空值防护
        if (Application.Current is not App app)
        {
            GetCamVm(camIndex).StatusText = "应用上下文异常";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：应用上下文为空", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 4. 设备管理器获取（异常防护）
        IDeviceManager deviceManager;
        try
        {
            deviceManager = app.Services.GetRequiredService<IDeviceManager>();
        }
        catch (InvalidOperationException ex)
        {
            GetCamVm(camIndex).StatusText = "服务获取失败";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：获取设备管理器失败\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 5. 工厂列表空值/索引防护
        var factories = CameraFactorySelector.GetCameraFactories(deviceManager);

        // Factory ordering can change between runs (e.g., plugin load failure), so prefer stable lookup by DeviceType.
        var idx = selection.FactoryIndex;
        if (!string.IsNullOrWhiteSpace(selection.FactoryDeviceType))
        {
            var byType = factories
                .Select((f, i) => (f, i))
                .FirstOrDefault(x => string.Equals(x.f.DeviceType, selection.FactoryDeviceType, StringComparison.OrdinalIgnoreCase));

            if (byType.f is not null)
                idx = byType.i;
        }

        if (factories == null || factories.Count == 0)
        {
            GetCamVm(camIndex).StatusText = "无可用相机工厂";
            UpdateCameraPanelState(camIndex);
            return;
        }

        if (idx < 0 || idx >= factories.Count)
        {
            GetCamVm(camIndex).StatusText = "工厂索引无效";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：工厂索引{idx}超出范围（0~{factories.Count - 1}）\n" +
                            $"已保存的DeviceType={selection.FactoryDeviceType}",
                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var factory = factories[idx];
        if (factory == null)
        {
            GetCamVm(camIndex).StatusText = "相机工厂为空";
            UpdateCameraPanelState(camIndex);
            return;
        }

        var duplicatedCamIndex = FindDuplicateMappedCamera(camIndex, selection, factory);
        if (duplicatedCamIndex is not null)
        {
            _cameraIndexToSelection[camIndex] = null;
            SaveDeviceMapping();
            GetCamVm(camIndex).StatusText = "设备重复映射";
            _cameraCheckStates[camIndex] = false;
            GetCamVm(camIndex).IsChecked = false;
            ClearCameraDisplay(camIndex);
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：该物理相机已分配给相机{duplicatedCamIndex}，不能重复绑定。",
                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 6. 创建设备（捕获关键异常）
        Core.Interfaces.IDevice? device = null;
        try
        {
            device = deviceManager.CreateDevice(factory.DeviceType, selection.DeviceId);
        }
        catch (KeyNotFoundException ex)
        {
            GetCamVm(camIndex).StatusText = "设备未找到";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：{ex.Message}\n\n请检查：\n1. 相机是否连接上电\n2. 重新枚举设备后再试",
                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            // 清空无效映射
            _cameraIndexToSelection[camIndex] = null;
            SaveDeviceMapping();
            return;
        }
        catch (ArgumentNullException ex)
        {
            GetCamVm(camIndex).StatusText = "设备ID为空";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (InvalidOperationException ex)
        {
            GetCamVm(camIndex).StatusText = "设备创建失败";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (Exception ex)
        {
            GetCamVm(camIndex).StatusText = "设备创建异常";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 7. 设备类型检查 + 空值释放
        if (device is not ICamera camera)
        {
            device?.Dispose(); // 空值合并运算符避免空引用
            GetCamVm(camIndex).StatusText = "设备类型不匹配";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：所选设备不是相机类型", "警告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 8. 初始化新相机
        try
        {
            camera.FrameReady += _frameHandlersByIndex[camIndex];
            _camerasByIndex[camIndex] = camera;
            GetCamVm(camIndex).StatusText = "未连接";
            _cameraCheckStates[camIndex] = false;
            GetCamVm(camIndex).IsChecked = false;
            ClearCameraDisplay(camIndex);
            UpdateCameraPanelState(camIndex);
        }
        catch (Exception ex)
        {
            camera.Dispose();
            _camerasByIndex[camIndex] = null;
            GetCamVm(camIndex).StatusText = "相机初始化失败";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}初始化失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplyMappingsAsync()
    {
        await SetCameraMappingAsync(1, _cameraIndexToSelection.GetValueOrDefault(1));
        await SetCameraMappingAsync(2, _cameraIndexToSelection.GetValueOrDefault(2));
        await SetCameraMappingAsync(3, _cameraIndexToSelection.GetValueOrDefault(3));
        await SetCameraMappingAsync(4, _cameraIndexToSelection.GetValueOrDefault(4));
        await InitializeMappedCamerasAsync();
        UpdateAllCameraPanelStates();
    }

    private async Task InitializeMappedCamerasAsync()
    {
        for (var camIndex = 1; camIndex <= 4; camIndex++)
        {
            if (_cameraIndexToSelection.GetValueOrDefault(camIndex) is null)
                continue;

            var cam = _camerasByIndex.GetValueOrDefault(camIndex);
            if (cam is null)
                continue;

            _cameraCheckStates[camIndex] = true;
            await SetCameraConnectedAsync(camIndex, true);

            var vm = GetCamVm(camIndex);
            if (!vm.IsChecked)
                vm.IsChecked = true;

            await StartGrabAsync(camIndex);
        }
    }

    private int? FindDuplicateMappedCamera(int camIndex, CameraMappingSelection selection, IDeviceFactory factory)
    {
        if (factory.IsCloneable || string.IsNullOrWhiteSpace(selection.DeviceId))
            return null;

        foreach (var kv in _cameraIndexToSelection)
        {
            if (kv.Key == camIndex || kv.Value is null)
                continue;

            var other = kv.Value;
            var sameFactory = !string.IsNullOrWhiteSpace(selection.FactoryDeviceType)
                ? string.Equals(other.FactoryDeviceType, selection.FactoryDeviceType, StringComparison.OrdinalIgnoreCase)
                : other.FactoryIndex == selection.FactoryIndex;

            if (!sameFactory)
                continue;

            if (string.Equals(other.DeviceId, selection.DeviceId, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        return null;
    }

    private async void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DeviceMappingWindow(_cameraIndexToSelection, _camerasByIndex)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var kv in dlg.CameraIndexToSelection)
                _cameraIndexToSelection[kv.Key] = kv.Value;

            SaveDeviceMapping();
            await ApplyMappingsAsync();
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            SaveDeviceMapping();
            var plcIp = PlcIpTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(plcIp)
                && int.TryParse(PlcPortTextBox.Text?.Trim(), out var plcPort)
                && plcPort is >= 1 and <= 65535)
            {
                SavePlcConnectionSettings(plcIp, plcPort);
            }

            for (var i = 1; i <= 4; i++)
            {
                if (_camerasByIndex[i] is { } cam)
                {
                    try { await cam.StopGrabbingAsync(); } catch { }
                    try { await cam.DisconnectAsync(); } catch { }
                    try { await cam.DisposeAsync(); } catch { }
                    try { cam.Dispose(); } catch { }
                }
            }

            foreach (var processor in _processorsByIndex.Values)
            {
                try { processor.Dispose(); } catch { }
            }

            try { _plc?.Dispose(); } catch { }
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private void ClearPlcRxLog_Click(object sender, RoutedEventArgs e)
    {
        PlcRxLogTextBox.Clear();
        _autoScrollRx = true;
        PlcRxLogTextBox.ScrollToEnd();
    }

    private void ClearPlcTxLog_Click(object sender, RoutedEventArgs e)
    {
        PlcTxLogTextBox.Clear();
        _autoScrollTx = true;
        PlcTxLogTextBox.ScrollToEnd();
    }

    // Optionally used later
    private void AppendLine(System.Windows.Controls.TextBox textBox, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        textBox.AppendText(line + Environment.NewLine);

        if (ReferenceEquals(textBox, PlcRxLogTextBox))
        {
            if (_autoScrollRx)
                textBox.ScrollToEnd();
            return;
        }

        if (ReferenceEquals(textBox, PlcTxLogTextBox))
        {
            if (_autoScrollTx)
                textBox.ScrollToEnd();
            return;
        }

        textBox.ScrollToEnd();
    }
}
