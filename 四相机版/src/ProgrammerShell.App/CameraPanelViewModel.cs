using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ProgrammerShell.App;

public partial class CameraPanelViewModel : ObservableObject
{
    public CameraPanelViewModel(int cameraIndex)
    {
        CameraIndex = cameraIndex;
        _title = $"{cameraIndex}号相机";
    }

    public int CameraIndex { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private int _captureCount;

    [ObservableProperty]
    private bool _startEnabled;

    [ObservableProperty]
    private bool _stopEnabled;

    [ObservableProperty]
    private bool _softTriggerEnabled;

    [ObservableProperty]
    private bool _resetEnabled;

    public IAsyncRelayCommand StartCommand { get; internal set; } = null!;
    public IAsyncRelayCommand StopCommand { get; internal set; } = null!;
    public IAsyncRelayCommand SoftTriggerCommand { get; internal set; } = null!;
    public IRelayCommand ResetCountCommand { get; internal set; } = null!;
}
