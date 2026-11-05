using MvCameraControl;
using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;
using System.Runtime.InteropServices;


namespace ProgrammerShell.Plugins.RealProgrammer.Camera.MVSCamera
{
    public sealed class Factory : IDeviceFactoryWithSelection
    {
        private const DeviceTLayerType devLayerType = DeviceTLayerType.MvGigEDevice |
                                                      DeviceTLayerType.MvGenTLCameraLinkDevice |
                                                      DeviceTLayerType.MvGenTLCXPDevice |
                                                      DeviceTLayerType.MvGenTLXoFDevice |
                                                      DeviceTLayerType.MvUsbDevice;

        private readonly ILoggerFactory? _loggerFactory;
        private readonly ILogger? _logger;
        private List<IDeviceInfo> _cachedDeviceInfos = new List<IDeviceInfo>();

        public Factory(ILoggerFactory? loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory?.CreateLogger<Factory>();

            // 初始化SDK并做空值/异常防护
            try
            {   
                int result = SDKSystem.Initialize();
                if (result == MvError.MV_OK)
                {
                    _logger?.LogInformation("SDK初始化成功");
                }
                else
                {
                    _logger?.LogError("SDK初始化失败，错误码：0x{Result:X}", result);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MVS SDK初始化失败");
            }
        }

        public string DeviceType => "mvs-camera";
        public string DisplayName => "MVS Industrial Camera";
        public bool IsCloneable => false;

        public Task<bool> DiscoverAsync(CancellationToken token = default)
        {
            try
            {
                // 每次枚举前清空缓存，避免旧数据干扰
                _cachedDeviceInfos.Clear();

                _logger?.LogInformation("开始枚举MVS相机设备...");
                int result = DeviceEnumerator.EnumDevices(devLayerType, out var devInfoList);

                _logger?.LogInformation("MVS枚举结果：错误码=0x{Result:X}，原始设备数={Count}",
                    result, devInfoList?.Count ?? 0);

                // 枚举结果空值处理
                if (result != MvError.MV_OK)
                {
                    _logger?.LogError("设备枚举失败，错误码：0x{Result:X}", result);
                    return Task.FromResult(false);
                }

                // 空列表防护 + 非空过滤 + 序列号去重
                if (devInfoList == null)
                {
                    _logger?.LogWarning("设备枚举返回空列表，错误码：0x{Result:X}", result);
                    _cachedDeviceInfos = new List<IDeviceInfo>();
                    return Task.FromResult(true);
                }

                // 过滤空设备信息 + 空序列号设备
                _cachedDeviceInfos = devInfoList
                    .Where(d => d != null && !string.IsNullOrWhiteSpace(d.SerialNumber))
                    .DistinctBy(d => d.SerialNumber) // 按序列号去重，避免重复设备
                    .ToList();

                // 打印枚举到的设备详情（调试关键）
                foreach (var dev in _cachedDeviceInfos)
                {
                    _logger?.LogInformation(
                        "枚举到相机：SerialNumber={Serial}, Model={Model}, UserName={UserName}, TLayer={TLayer}",
                        dev.SerialNumber, dev.ModelName, dev.UserDefinedName, dev.TLayerType);
                }

                _logger?.LogInformation("最终枚举到{Count}个有效MVS相机设备", _cachedDeviceInfos.Count);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "设备枚举异常");
                return Task.FromResult(false);
            }
        }

        public IReadOnlyList<DeviceDescriptor> GetAvailableDevices()
        {
            // 空值防护：缓存为空时返回空列表
            if (_cachedDeviceInfos == null || _cachedDeviceInfos.Count == 0)
            {
                _logger?.LogWarning("获取可用设备时缓存为空");
                return new List<DeviceDescriptor>().AsReadOnly();
            }

            return _cachedDeviceInfos.Select(d =>
            {
                // 空值处理：设备信息字段为空时的默认值
                string displayName = string.IsNullOrEmpty(d.UserDefinedName)
                    ? $"{d.TLayerType}: {d.ManufacturerName ?? "未知厂商"} {d.ModelName ?? "未知型号"}"
                    : d.UserDefinedName;
                return new DeviceDescriptor(d.SerialNumber, $"{displayName} ({d.SerialNumber})");
            }).ToList().AsReadOnly();
        }

        public Core.Interfaces.IDevice CreateDevice(ILogger logger)
        {
            // 空缓存防护：尝试重新枚举
            if (_cachedDeviceInfos == null || _cachedDeviceInfos.Count == 0)
            {
                logger.LogWarning("未枚举到可用MVS相机，尝试重新枚举...");
                var discoverTask = DiscoverAsync(CancellationToken.None);
                discoverTask.Wait(2000); // 等待2秒，避免阻塞过久

                if (_cachedDeviceInfos == null || _cachedDeviceInfos.Count == 0)
                    throw new InvalidOperationException("未枚举到可用MVS相机");
            }

            // 取第一个设备前做空值检查
            var firstDevice = _cachedDeviceInfos.FirstOrDefault();
            if (firstDevice == null)
                throw new InvalidOperationException("枚举到的第一个相机设备信息为空");

            return new MVSCameraDevice(logger, firstDevice);
        }

        public Core.Interfaces.IDevice CreateDevice(ILogger logger, string? deviceId)
        {
            // 设备ID空值防护
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentNullException(nameof(deviceId), "MVS相机设备ID不能为空");

            // 缓存空值防护
            if (_cachedDeviceInfos == null || _cachedDeviceInfos.Count == 0)
                throw new InvalidOperationException("未枚举到任何MVS相机设备，无法创建指定ID的设备");

            // 打印调试信息，便于定位ID匹配问题
            logger.LogInformation("尝试创建设备，请求ID={DeviceId}，当前缓存设备数={Count}",
                deviceId, _cachedDeviceInfos.Count);

            foreach (var dev in _cachedDeviceInfos)
            {
                logger.LogInformation("缓存设备：{Serial} (请求ID：{DeviceId})",
                    dev?.SerialNumber ?? "空", deviceId);
            }

            // 按序列号匹配，忽略大小写
            var deviceInfo = _cachedDeviceInfos.FirstOrDefault(d =>
                d != null && string.Equals(d.SerialNumber, deviceId, StringComparison.OrdinalIgnoreCase));

            // 设备未找到时抛出详细异常
            if (deviceInfo == null)
            {
                var availableIds = _cachedDeviceInfos
                    .Where(d => d != null)
                    .Select(d => d.SerialNumber)
                    .ToList();
                var availableIdsStr = availableIds.Count > 0
                    ? string.Join(", ", availableIds)
                    : "无";

                throw new KeyNotFoundException(
                    $"未找到设备ID为{deviceId}的MVS相机。\n可用设备ID：{availableIdsStr}");
            }

            logger.LogInformation("成功找到设备：Serial={Serial}, Model={Model}",
                deviceInfo.SerialNumber, deviceInfo.ModelName);

            return new MVSCameraDevice(logger, deviceInfo);
        }
    }
}