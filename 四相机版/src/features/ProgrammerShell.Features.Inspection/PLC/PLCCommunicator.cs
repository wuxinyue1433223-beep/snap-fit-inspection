using System.Net.Sockets;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.Features.Inspection.PLC;

/// <summary>
/// 简单的 PLC 通信器（TCP），提供写入与异步删除接口
/// </summary>
public sealed class PLCCommunicator : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private readonly object _lock = new();
    private readonly string _plcIP;
    private readonly int _plcPort;
    private bool _isConnected;
    private Thread? _rxThread;
    private CancellationTokenSource? _rxCts;
    private CancellationTokenSource? _autoConnectCts;
    private Task? _autoConnectTask;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _reconnectInterval;
    private bool _disposed;
    public event Action<int>? OnPlcTriggerCamera; // 触发事件，参数为相机编号(1-4)
    public event Action<IReadOnlyList<int>>? OnPlcTriggerBatch;

    // PLC protocol (8 bytes, no header/tail)
    // Byte layout (pairs):
    // cam1 => [0]=cameraId [1]=result/command
    // cam2 => [2]=cameraId [3]=result/command
    // cam3 => [4]=cameraId [5]=result/command
    // cam4 => [6]=cameraId [7]=result/command
    private readonly Dictionary<int, int> _bytePositions = new()
    {
        { 1, 0 },
        { 2, 2 },
        { 3, 4 },
        { 4, 6 }
    };

    // Result mapping per spec:
    // 0x00 => NG
    // 0x01 => OK
    // (Other values reserved)
    private readonly Dictionary<string, byte> _resultValues = new(StringComparer.OrdinalIgnoreCase)
    {
        { "NG", 0 },
        { "OK", 1 },
        // Compatibility with current detection pipeline
        { "OK1", 1 },
        { "OK2", 1 }
    };

    private readonly Action<string>? _txLog;
    private readonly Action<string>? _rxLog;

    public PLCCommunicator(
        string plcIP = "192.168.250.60",
        int plcPort = 12000,
        Action<string>? txLog = null,
        Action<string>? rxLog = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? reconnectInterval = null)
    {
        _plcIP = plcIP;
        _plcPort = plcPort;
        _txLog = txLog;
        _rxLog = rxLog;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _reconnectInterval = reconnectInterval ?? TimeSpan.FromSeconds(1);
        _isConnected = false;
        StartAutoConnectLoop();
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _isConnected;
            }
        }
    }

    private bool Connect()
    {
        lock (_lock)
        {
            try
            {
                if (_disposed)
                    return false;

                _txLog?.Invoke($"Connecting to PLC {_plcIP}:{_plcPort} ...");
                _tcpClient?.Close();
                _tcpClient = new TcpClient
                {
                    ReceiveTimeout = 5000,
                    SendTimeout = 5000
                };

                var t = _tcpClient.ConnectAsync(_plcIP, _plcPort);
                if (!t.Wait(_connectTimeout))
                {
                    try { _tcpClient.Close(); } catch { }
                    _tcpClient = null;
                    _networkStream = null;
                    _isConnected = false;
                    _txLog?.Invoke($"Connect timeout after {_connectTimeout.TotalMilliseconds:0}ms");
                    return false;
                }

                if (t.IsFaulted)
                {
                    _isConnected = false;
                    _txLog?.Invoke($"Connect failed: {t.Exception?.GetBaseException().Message}");
                    return false;
                }

                _networkStream = _tcpClient.GetStream();
                _isConnected = true;
                _txLog?.Invoke("Connected to PLC");

                StartRxLoop();
                return true;
            }
            catch
            {
                _isConnected = false;
                _txLog?.Invoke("Connect failed");
                return false;
            }
        }
    }

    private void StartAutoConnectLoop()
    {
        _autoConnectCts = new CancellationTokenSource();
        var ct = _autoConnectCts.Token;

        _autoConnectTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var connected = IsConnected;
                if (!connected)
                {
                    Connect();
                }

                try
                {
                    await Task.Delay(_reconnectInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, ct);
    }

    private void MarkDisconnected(string reason)
    {
        lock (_lock)
        {
            if (!_isConnected && _networkStream is null && _tcpClient is null)
                return;

            _isConnected = false;

            try { _networkStream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _networkStream = null;
            _tcpClient = null;
        }

        _txLog?.Invoke($"PLC disconnected: {reason}");
    }

    public bool WriteColorStatus(int camId, string result)
    {
        return WriteBatchStatus(new Dictionary<int, string> { [camId + 1] = result }, new[] { camId + 1 });
    }

    public bool WriteRawFrame(byte[] frame)
    {
        try
        {
            if (frame is null || frame.Length != 8)
                return false;

            _txLog?.Invoke($"TX (8B): frame={Convert.ToHexString(frame)}");

            lock (_lock)
            {
                if (!_isConnected || _networkStream == null)
                {
                    _txLog?.Invoke("TX skipped: PLC not connected");
                    return false;
                }

                try
                {
                    _networkStream!.Write(frame, 0, frame.Length);
                    _txLog?.Invoke("TX ok");
                    return true;
                }
                catch
                {
                    MarkDisconnected("write failed");
                    _txLog?.Invoke("TX failed");
                    return false;
                }
            }
        }
        catch
        {
            _txLog?.Invoke("TX error");
            return false;
        }
    }

    public bool WriteBatchStatus(IReadOnlyDictionary<int, string> cameraResults, IEnumerable<int> triggeredCameraIds)
    {
        try
        {
            var triggered = triggeredCameraIds
                .Where(id => id is >= 1 and <= 4)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            var frame = new byte[8];
            foreach (var camId in triggered)
            {
                if (!_bytePositions.TryGetValue(camId, out var pos))
                    continue;

                // 协议约定：
                // - 有检测结果：写 [camId, result]
                // - 无检测结果：写 [00, 00]
                if (cameraResults.TryGetValue(camId, out var result)
                    && _resultValues.TryGetValue(result, out var resultValue))
                {
                    frame[pos] = (byte)camId;
                    frame[pos + 1] = resultValue;
                }
            }

            return WriteRawFrame(frame);
        }
        catch
        {
            _txLog?.Invoke("TX error");
            return false;
        }
    }

    public bool DeleteImage(int camId, string imagePath)
    {
        try
        {
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public void QueueImageDeletion(int camId, string imagePath)
    {
        Task.Run(() => DeleteImage(camId, imagePath));
    }

    public void Dispose()
    {
        _disposed = true;

        try { _autoConnectCts?.Cancel(); } catch { }

        lock (_lock)
        {
            try { _rxCts?.Cancel(); } catch { }

            try { _networkStream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _networkStream = null;
            _tcpClient = null;
            _isConnected = false;
        }

        try { _autoConnectTask?.Wait(300); } catch { }
        _autoConnectTask = null;
        _autoConnectCts?.Dispose();
        _autoConnectCts = null;

        try { _rxThread?.Join(200); } catch { }
        _rxThread = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    private void ParseRxTriggerCommand(byte[] rxData)
    {
        try
        {
            if (rxData.Length != 8)
            {
                _rxLog?.Invoke($"RX: invalid length={rxData.Length} (expect 8)");
                return;
            }

            var triggered = new List<int>(4);
            for (var camId = 1; camId <= 4; camId++)
            {
                var pos = _bytePositions[camId];
                var idByte = rxData[pos];
                var cmd = rxData[pos + 1];

                // Spec: RX id byte fixed to camId; cmd=1 triggers
                if (idByte == camId && cmd == 1)
                {
                    triggered.Add(camId);
                }
            }

            if (triggered.Count > 0)
            {
                OnPlcTriggerBatch?.Invoke(triggered.AsReadOnly());

                foreach (var camId in triggered)
                {
                    _rxLog?.Invoke($"RX trigger: cam={camId}");
                    OnPlcTriggerCamera?.Invoke(camId);
                }
            }
        }
        catch (Exception ex)
        {
            _rxLog?.Invoke($"RX: 解析指令失败: {ex.Message}");
        }
    }

    private void StartRxLoop()
    {
        // restart thread if already running
        try { _rxCts?.Cancel(); } catch { }
        _rxCts?.Dispose();
        _rxCts = new CancellationTokenSource();

        var ct = _rxCts.Token;
        _rxThread = new Thread(() =>
        {
            var buffer = new byte[1024];
            var rxBuf = new List<byte>(2048);
            while (!ct.IsCancellationRequested)
            {
                NetworkStream? stream;
                lock (_lock)
                {
                    stream = _networkStream;
                }

                if (stream is null || !_isConnected)
                {
                    Thread.Sleep(100);
                    continue;
                }

                try
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        MarkDisconnected("remote closed");
                        Thread.Sleep(50);
                        continue;
                    }

                    rxBuf.AddRange(buffer.AsSpan(0, read).ToArray());

                    while (rxBuf.Count >= 8)
                    {
                        var frame = rxBuf.GetRange(0, 8).ToArray();
                        rxBuf.RemoveRange(0, 8);
                        _rxLog?.Invoke($"RX (8B): frame={Convert.ToHexString(frame)}");
                        ParseRxTriggerCommand(frame);
                    }
                }
                catch (IOException ex)
                {
                    if (IsReadTimeout(ex))
                    {
                        // Read timeout means no incoming data yet, not a disconnection.
                        Thread.Sleep(20);
                        continue;
                    }

                    MarkDisconnected("read failed");
                    Thread.Sleep(100);
                }
                catch (ObjectDisposedException)
                {
                    MarkDisconnected("stream disposed");
                    break;
                }
                catch
                {
                    MarkDisconnected("read exception");
                    Thread.Sleep(100);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PLC-RX"
        };

        _rxThread.Start();
    }

    private static bool IsReadTimeout(IOException ex)
    {
        if (ex.InnerException is SocketException se)
            return se.SocketErrorCode == SocketError.TimedOut;

        return false;
    }

}
