using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace ProgrammerShell.App;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _mainContent;

    public MainWindowViewModel()
    {
        // Default content can be set by features (e.g., Flasher) or left null.
        _mainContent = null;

        Cameras = new ObservableCollection<CameraPanelViewModel>
        {
            new(1),
            new(2),
            new(3),
            new(4),
        };
    }

    public ObservableCollection<CameraPanelViewModel> Cameras { get; }

    [ObservableProperty]
    private string _plcIp = "192.168.0.10";

    [ObservableProperty]
    private int _plcPort = 502;

    [ObservableProperty]
    private string _plcRxLog = string.Empty;

    [ObservableProperty]
    private string _plcTxLog = string.Empty;
}