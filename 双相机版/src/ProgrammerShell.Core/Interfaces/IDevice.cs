using Microsoft.Extensions.Logging;

namespace ProgrammerShell.Core.Interfaces;

public interface IDevice : IAsyncDisposable, IDisposable
{
 string DeviceId { get; }
 string Name { get; }
 DeviceState State { get; }

 Task ConnectAsync(CancellationToken token = default);
 Task DisconnectAsync(CancellationToken token = default);
}
