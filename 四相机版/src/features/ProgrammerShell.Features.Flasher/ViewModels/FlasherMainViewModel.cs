using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.Features.Flasher.ViewModels;

public partial class FlasherMainViewModel : ObservableObject
{
 private readonly ILogger<FlasherMainViewModel> _logger;
 private readonly IDeviceManager _deviceManager;

 public FlasherMainViewModel(ILogger<FlasherMainViewModel> logger, IDeviceManager deviceManager)
 {
 _logger = logger;
 _deviceManager = deviceManager;
 }

 [RelayCommand]
 private void Action1()
 {
 _logger.LogInformation("Action1 invoked. Factories: {FactoryCount}, ActiveDevices: {DeviceCount}",
 _deviceManager.Factories.Count,
 _deviceManager.ActiveDevices.Count);
 }

 [RelayCommand]
 private void Action2()
 {
 _logger.LogInformation("Action2 invoked.");
 }
}
