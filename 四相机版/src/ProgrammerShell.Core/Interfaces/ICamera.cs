namespace ProgrammerShell.Core.Interfaces;

public interface ICamera : IDevice
{
 Task StartGrabbingAsync(CancellationToken token = default);
 Task StopGrabbingAsync(CancellationToken token = default);
 Task SoftTriggerAsync(CancellationToken token = default);
 string? LastCapturedImagePath { get; }

 /// <summary>
 /// Raised when a new frame is available.
 /// Payload: BGRA32 pixels (width*height*4).
 /// </summary>
 event EventHandler<CameraFrame>? FrameReady;
}

public sealed record CameraFrame(int Width, int Height, byte[] Bgra32Pixels);
