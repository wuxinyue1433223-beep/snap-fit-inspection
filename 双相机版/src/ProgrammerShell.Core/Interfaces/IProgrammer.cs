namespace ProgrammerShell.Core.Interfaces;

public interface IProgrammer : IDevice
{
 Task ProgramAsync(byte[] firmware, CancellationToken token = default);
 Task VerifyAsync(byte[] firmware, CancellationToken token = default);
 Task<ProgrammerShell.Core.Models.ProgrammerInfo> GetInfoAsync(CancellationToken token = default);
}
