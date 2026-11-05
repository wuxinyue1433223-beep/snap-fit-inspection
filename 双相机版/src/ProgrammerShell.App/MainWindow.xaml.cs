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
    private readonly ILoggerFactory _loggerFactory;
    private readonly MainWindowViewModel _viewModel;

    private const int VisibleCameraCount = 2;
    private string _selectedPartType = "typeA";

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
    private long _plcBatchSequence;
    private long? _pendingPlcBatchId;
    private PLCCommunicator? _plc;
    private readonly SemaphoreSlim _plcBatchExecutionLock = new(1, 1);
    private readonly SemaphoreSlim _plcConnectLock = new(1, 1);
    private readonly CancellationTokenSource _plcConnectionMonitorCts = new();
    private Task? _plcConnectionMonitorTask;

    private const double AutoScrollThreshold = 48;
    private const int PreviewDecodePixelWidth = 1280;
    private bool _autoScrollRx = true;
    private bool _autoScrollTx = true;

    private sealed record PlcConnectionSettings(string? Ip, int? Port);

    public MainWindow(ILogger<MainWindow> logger, ILoggerFactory loggerFactory, MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _logger = logger;
        _loggerFactory = loggerFactory;
        _viewModel = viewModel;
        DataContext = viewModel;

        _logger.LogInformation("MainWindow started. LogPath={LogPath}", FileLoggerProvider.GetDefaultLogPath());

        _selectedPartType = GetSelectedPartType();
        RecreateImageProcessors();

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
        DisableUnusedCameraMappings();
        LoadPlcConnectionSettings();
        Loaded += async (_, __) =>
        {
            await ApplyMappingsAsync();
            StartPlcConnectionMonitor();
        };

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

    private static string GetPartTypeTemplatesRoot(string partType)
    {
        var root = GetSolutionRoot();
        return Path.Combine(root, "templates", partType);
    }

    private ComboBox? GetPartTypeCombo()
        => FindName("PartTypeCombo") as ComboBox;

    private string GetSelectedPartType()
    {
        if (GetPartTypeCombo()?.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "typeA";

        return _selectedPartType;
    }

    private void RecreateImageProcessors()
    {
        var templatesRoot = GetPartTypeTemplatesRoot(_selectedPartType);
        for (var i = 1; i <= 4; i++)
        {
            if (_processorsByIndex.TryGetValue(i, out var existing))
                existing.Dispose();

            _processorsByIndex[i] = new ProgrammerShell.Features.Inspection.Inspection.ImageProcessor(
                i - 1,
                plcCommunicator: null,
                configPath: templatesRoot,
                logger: _loggerFactory.CreateLogger<ProgrammerShell.Features.Inspection.Inspection.ImageProcessor>());
        }

        _logger?.LogInformation("Template root switched to {TemplatesRoot}", templatesRoot);
    }

    private void DisableUnusedCameraMappings()
    {
        _cameraIndexToSelection[3] = null;
        _cameraIndexToSelection[4] = null;
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

    private void PartTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedPartType();
        if (string.Equals(selected, _selectedPartType, StringComparison.OrdinalIgnoreCase))
            return;

        _selectedPartType = selected;
        RecreateImageProcessors();
    }

    private async void PlcConnectButton_Click(object sender, RoutedEventArgs e)
        => await ConnectPlcAsync(isManual: true, _plcConnectionMonitorCts.Token);

    private void StartPlcConnectionMonitor()
    {
        if (_plcConnectionMonitorTask is { IsCompleted: false })
            return;

        _plcConnectionMonitorTask = Task.Run(() => MonitorPlcConnectionAsync(_plcConnectionMonitorCts.Token));
    }

    private async Task MonitorPlcConnectionAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(3);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var isConnected = await Dispatcher.InvokeAsync(() => _plc?.IsConnected == true);
                if (isConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                var connectTask = await Dispatcher.InvokeAsync(() => ConnectPlcAsync(isManual: false, cancellationToken));
                var connectedNow = await connectTask;

                if (connectedNow)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    AppendLine(PlcTxLogTextBox, $"PLC自动连接异常: {ex.Message}"));
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private async Task<bool> ConnectPlcAsync(bool isManual, CancellationToken cancellationToken)
    {
        var ip = PlcIpTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            if (isManual)
                MessageBox.Show("PLC IP 为空", "PLC");
            else
                AppendLine(PlcTxLogTextBox, "PLC自动连接失败: IP为空");

            return false;
        }

        if (!int.TryParse(PlcPortTextBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            if (isManual)
                MessageBox.Show("PLC 端口不合法", "PLC");
            else
                AppendLine(PlcTxLogTextBox, "PLC自动连接失败: 端口不合法");

            return false;
        }

        await _plcConnectLock.WaitAsync(cancellationToken);
        PlcConnectButton.IsEnabled = false;

        try
        {
            if (_plc?.IsConnected == true)
                return true;

            var newPlc = await Task.Run(() =>
            {
                return new PLCCommunicator(
                    ip,
                    port,
                    txLog: msg => _ = Dispatcher.BeginInvoke(() => AppendLine(PlcTxLogTextBox, msg)),
                    rxLog: msg => _ = Dispatcher.BeginInvoke(() => AppendLine(PlcRxLogTextBox, msg)),
                    connectTimeout: TimeSpan.FromSeconds(2));
            }, cancellationToken);

            if (!newPlc.IsConnected)
            {
                newPlc.Dispose();
                if (isManual)
                    MessageBox.Show("PLC连接失败，请检查PLC网络和端口设置。", "PLC");
                AppendLine(PlcTxLogTextBox, $"PLC连接失败: {ip}:{port}");
                return false;
            }

            try { _plc?.Dispose(); } catch { }
            _plc = newPlc;

            _plc.OnPlcTriggerBatch += (triggeredCameras, typeSelector) =>
            {
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    await HandlePlcBatchTriggerAsync(triggeredCameras, typeSelector);
                });
            };

            RecreateImageProcessors();
            AppendLine(PlcTxLogTextBox, $"PLC已连接: {ip}:{port}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppendLine(PlcTxLogTextBox, $"PLC connect error: {ex.GetBaseException().Message}");

            if (isManual)
                MessageBox.Show($"PLC连接异常: {ex.GetBaseException().Message}", "PLC");

            return false;
        }
        finally
        {
            PlcConnectButton.IsEnabled = true;
            _plcConnectLock.Release();
        }
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
        // PLC settings file is managed as read-only configuration.
        // Keep this method as no-op so existing call sites don't persist changes.
    }

    private void SaveDeviceMapping()
    {
        // Device mapping file is managed as read-only configuration.
        // Keep this method as no-op so existing call sites don't persist changes.
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

            await RunDetectionAndSaveAsync(camIndex, null, triggeredAtUtc, capturedPath, plcBatchId, allowRetry: true);
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

        _ = Dispatcher.BeginInvoke(() => UpdateCameraImage(camIndex, frame));
    }

    private void UpdateCameraImage(int camIndex, CameraFrame frame)
    {
        if (camIndex is < 1 or > VisibleCameraCount)
            return;

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
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(name)
            ? null
            : FindName(name) as System.Windows.Controls.TextBlock;
    }

    private async Task RunDetectionAndSaveAsync(int camIndex, CameraFrame? frame, DateTime triggeredAtUtc, string? capturedPath = null, long? plcBatchId = null, bool allowRetry = true)
    {
        var cam = _camerasByIndex.GetValueOrDefault(camIndex);
        var root = GetSolutionRoot();
        var tempDir = Path.Combine(root, "photo", "temp-trigger");
        Directory.CreateDirectory(tempDir);
        var processingFile = Path.Combine(tempDir, $"cam_{camIndex - 1}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.bmp");
        string? sourceFile = null;

        var useSavedTriggerImage = false;
        try
        {
            var preferredPath = !string.IsNullOrWhiteSpace(capturedPath) ? capturedPath : cam?.LastCapturedImagePath;
            if (!string.IsNullOrWhiteSpace(preferredPath)
                && File.Exists(preferredPath)
                && File.GetLastWriteTimeUtc(preferredPath) >= triggeredAtUtc)
            {
                sourceFile = preferredPath;
                useSavedTriggerImage = true;
            }
        }
        catch
        {
            useSavedTriggerImage = false;
            sourceFile = null;
        }

        if (useSavedTriggerImage && !string.IsNullOrWhiteSpace(sourceFile))
        {
            _logger.LogInformation("Detection source selected: cam={Cam}, source={Source}, useSavedTriggerImage={UseSavedTriggerImage}, processing={Processing}", camIndex, sourceFile, true, processingFile);
            await CopyProcessingSnapshotAsync(sourceFile, processingFile);
            _logger.LogInformation("Detection snapshot copied: cam={Cam}, source={Source}, processing={Processing}", camIndex, sourceFile, processingFile);
            
            // Cleanup source file right after copying if it's in temp-trigger to avoid accumulation
            try 
            {
                if (Path.GetDirectoryName(sourceFile)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Equals(tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
                {
                    File.Delete(sourceFile);
                }
            }
            catch { /* ignore */ }
        }
        else
        {
            if (frame is null)
            {
                _logger.LogWarning("Detection skipped because no saved trigger image and no frame fallback: cam={Cam}", camIndex);
                return;
            }

            await Task.Run(() =>
            {
                using var bgra = CreateMatFromFrame(frame);
                using var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                Cv2.ImWrite(processingFile, bgr);
            });

            _logger.LogInformation("Detection processing snapshot created from frame: cam={Cam}, processing={Processing}", camIndex, processingFile);
        }

        if (!_processorsByIndex.TryGetValue(camIndex, out var processor))
        {
            _logger.LogWarning("Detection processor not found: cam={Cam}", camIndex);
            // Also cleanup processing file here if early exit
            try { if (File.Exists(processingFile)) File.Delete(processingFile); } catch { }
            return;
        }

        DetectionResult res;
        try
        {
            res = await processor.ProcessImageAsync(processingFile);
            _logger.LogInformation("Detection first pass done: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);

            if (allowRetry && IsZeroTemplateNg(res))
            {
                _logger.LogWarning("Detection retry because zero-template NG: cam={Cam}, detail={Detail}", camIndex, res.ColorDetected);
                var retry = await processor.ProcessImageAsync(processingFile);
                if (!IsZeroTemplateNg(retry) || string.Equals(retry.Result, "OK", StringComparison.OrdinalIgnoreCase))
                    res = retry;

                _logger.LogInformation("Detection retry result after zero-template NG: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);
            }

            if (allowRetry && IsProcessingError(res))
            {
                _logger.LogWarning("Detection retry because processing error: cam={Cam}", camIndex);
                var retry = await processor.ProcessImageAsync(processingFile);
                if (!IsProcessingError(retry))
                    res = retry;

                _logger.LogInformation("Detection retry result after processing error: cam={Cam}, result={Result}, detail={Detail}, plc={Plc}", camIndex, res.Result, res.ColorDetected, res.PLCResult);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(processingFile))
                    File.Delete(processingFile);
            }
            catch
            {
            }
        }

        if (string.Equals(res.ColorDetected, "No Light", StringComparison.OrdinalIgnoreCase))
            res.ColorDetected = "Processing Error";

        var isOk = string.Equals(res.Result, "OK", StringComparison.OrdinalIgnoreCase);
        var displayText = $"{res.Result} - {res.ColorDetected} ({res.PLCResult})";
        _logger.LogInformation("Detection done: cam={Cam}, result={Result}, plc={Plc}, detail={Detail}, save={Save}", camIndex, res.Result, res.PLCResult, res.ColorDetected, res.SavePath);
        TrySendPlcBatchResult(camIndex, res.PLCResult, plcBatchId);
        Dispatcher.BeginInvoke(() => UpdateCameraResultText(camIndex, displayText, isOk));

        // If the detector produced an annotated image on disk, show it so manual and PLC trigger paths
        // present the same visual result with template-match overlays.
        if (!string.IsNullOrWhiteSpace(res.SavePath) && File.Exists(res.SavePath))
        {
            Dispatcher.BeginInvoke(() => UpdateCameraImageFromFile(camIndex, res.SavePath!));
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

    private static async Task CopyProcessingSnapshotAsync(string sourcePath, string targetPath)
    {
        const int maxRetries = 8;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch when (attempt < maxRetries - 1)
            {
                await Task.Delay(25);
            }
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

    private static string? ResolvePlcPartType(byte typeSelector)
    {
        return typeSelector switch
        {
            1 => "typeA",
            2 => "typeB",
            _ => null
        };
    }

    private async Task HandlePlcBatchTriggerAsync(IReadOnlyList<int>? triggeredByPlc, byte typeSelector)
    {
        await _plcBatchExecutionLock.WaitAsync();
        try
        {
            var plcPartType = ResolvePlcPartType(typeSelector);
            if (!string.IsNullOrWhiteSpace(plcPartType)
                && !string.Equals(plcPartType, _selectedPartType, StringComparison.OrdinalIgnoreCase))
            {
                _selectedPartType = plcPartType;
                RecreateImageProcessors();
                AppendLine(PlcTxLogTextBox, $"PLC模板已切换为: {plcPartType}");
            }

            var requested = (triggeredByPlc ?? Array.Empty<int>())
                .Where(id => id is >= 1 and <= 4)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            if (requested.Length == 0)
                requested = new[] { 1, 2 };

            long batchId;
            lock (_plcBatchLock)
            {
                _plcBatchSequence++;
                batchId = _plcBatchSequence;
                _pendingPlcBatchId = batchId;
                _pendingPlcTriggeredCameras = new HashSet<int>(requested);
                _pendingPlcResults.Clear();
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
        finally
        {
            _plcBatchExecutionLock.Release();
        }
    }

    private void TrySendPlcBatchResult(int camIndex, string? plcResult, long? plcBatchId)
    {
        if (_plc is null)
            return;

        Dictionary<int, string>? batchResults = null;
        int[]? triggeredCameras = null;

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
            _pendingPlcBatchId = null;
            _pendingPlcTriggeredCameras = null;
            _pendingPlcResults.Clear();
        }

        _plc.WriteBatchStatus(batchResults, triggeredCameras);
    }

    private void UpdateCameraImageFromFile(int camIndex, string filePath)
    {
        if (camIndex is < 1 or > VisibleCameraCount)
            return;

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
            catch
            {
            }
            finally
            {
                _camerasByIndex[camIndex] = null;
            }
        }

        if (selection is null)
        {
            GetCamVm(camIndex).StatusText = "未配置";
            _cameraCheckStates[camIndex] = false;
            GetCamVm(camIndex).IsChecked = false;
            ClearCameraDisplay(camIndex);
            UpdateCameraPanelState(camIndex);
            return;
        }

        if (Application.Current is not App app)
        {
            GetCamVm(camIndex).StatusText = "应用上下文异常";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：应用上下文为空", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

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

        var factories = CameraFactorySelector.GetCameraFactories(deviceManager);

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

        Core.Interfaces.IDevice? device = null;
        try
        {
            device = deviceManager.CreateDevice(factory.DeviceType, selection.DeviceId);
        }
        catch (KeyNotFoundException ex)
        {
            GetCamVm(camIndex).StatusText = "设备未找到(保留映射)";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：{ex.Message}\n\n请检查：\n1. 相机是否连接上电\n2. 重新枚举设备后再试",
                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        if (device is not ICamera camera)
        {
            device?.Dispose();
            GetCamVm(camIndex).StatusText = "设备类型不匹配";
            UpdateCameraPanelState(camIndex);
            MessageBox.Show($"相机{camIndex}配置失败：所选设备不是相机类型", "警告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
        DisableUnusedCameraMappings();
        await SetCameraMappingAsync(1, _cameraIndexToSelection.GetValueOrDefault(1));
        await SetCameraMappingAsync(2, _cameraIndexToSelection.GetValueOrDefault(2));
        await SetCameraMappingAsync(3, null);
        await SetCameraMappingAsync(4, null);
        await InitializeMappedCamerasAsync();
        UpdateAllCameraPanelStates();
    }

    private async Task InitializeMappedCamerasAsync()
    {
        for (var camIndex = 1; camIndex <= VisibleCameraCount; camIndex++)
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

        foreach (var kv in _cameraIndexToSelection.Where(kv => kv.Key <= VisibleCameraCount))
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

            DisableUnusedCameraMappings();
            SaveDeviceMapping();
            await ApplyMappingsAsync();
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            try { _plcConnectionMonitorCts.Cancel(); } catch { }
            try { if (_plcConnectionMonitorTask is not null) await _plcConnectionMonitorTask; } catch { }

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
                if (_camerasByIndex.GetValueOrDefault(i) is { } cam)
                {
                    try { await cam.StopGrabbingAsync(); } catch { }
                    try { await cam.DisconnectAsync(); } catch { }
                    try { await cam.DisposeAsync(); } catch { }
                    try { cam.Dispose(); } catch { }
                }
            }

            try { _plc?.Dispose(); } catch { }
        }
        finally
        {
            _plcConnectionMonitorCts.Dispose();
            _plcConnectLock.Dispose();
            _plcBatchExecutionLock.Dispose();
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
