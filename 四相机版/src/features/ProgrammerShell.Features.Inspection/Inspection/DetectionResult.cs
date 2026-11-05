namespace ProgrammerShell.Features.Inspection.Inspection;

public sealed class DetectionResult
{
    public int CameraId { get; set; }
    public string Result { get; set; } = "NG"; // OK or NG
    public string PLCResult { get; set; } = "NG"; // OK or NG
    public string ColorDetected { get; set; } = "Unknown";
    public string? ImagePath { get; set; }
    public string? SavePath { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsFirstDetection { get; set; }
    public bool IsSuccess { get; set; }
}
