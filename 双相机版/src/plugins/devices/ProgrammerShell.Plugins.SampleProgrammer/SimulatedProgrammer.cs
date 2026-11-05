using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;
using ProgrammerShell.Core.Models;

namespace ProgrammerShell.Plugins.SampleProgrammer;

public sealed class SimulatedProgrammer : IProgrammer
{
 private readonly ILogger _logger;
 private DeviceState _state = DeviceState.Disconnected;

 public SimulatedProgrammer(ILogger logger)
 {
 _logger = logger;
 DeviceId = Guid.NewGuid().ToString("N");
 Name = "Simulated";
 }

 public string DeviceId { get; }
 public string Name { get; }
 public DeviceState State => _state;

 public async Task ConnectAsync(CancellationToken token = default)
 {
 _state = DeviceState.Connecting;
 await Task.Delay(150, token);
 _state = DeviceState.Connected;
 _logger.LogInformation("Connected {DeviceId}", DeviceId);
 }

 public async Task DisconnectAsync(CancellationToken token = default)
 {
 if (_state == DeviceState.Disconnected) return;
 await Task.Delay(50, token);
 _state = DeviceState.Disconnected;
 _logger.LogInformation("Disconnected {DeviceId}", DeviceId);
 }

 public async Task ProgramAsync(byte[] firmware, CancellationToken token = default)
 {
 _logger.LogInformation("Program started. Size={Size}", firmware?.Length ??0);
 await Task.Delay(300, token);
 _logger.LogInformation("Program finished.");
 }

 public async Task VerifyAsync(byte[] firmware, CancellationToken token = default)
 {
 _logger.LogInformation("Verify started. Size={Size}", firmware?.Length ??0);
 await Task.Delay(200, token);
 _logger.LogInformation("Verify finished.");
 }

 public Task<ProgrammerInfo> GetInfoAsync(CancellationToken token = default)
 {
 var info = new ProgrammerInfo(Model: "SimulatedModel", SerialNumber: $"SIM-{Random.Shared.Next(1000,9999)}", FirmwareVersion: "0.0");
 return Task.FromResult(info);
 }

 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
 public void Dispose() { }
}
