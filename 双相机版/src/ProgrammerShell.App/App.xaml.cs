using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProgrammerShell.App.Services;
using ProgrammerShell.Core.Interfaces;
using ProgrammerShell.Features.Flasher;

namespace ProgrammerShell.App;

public partial class App : Application
{
 private IHost? _host;

 public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");

 protected override async void OnStartup(StartupEventArgs e)
 {
 base.OnStartup(e);

 _host = Host.CreateDefaultBuilder(e.Args)
 .ConfigureLogging(logging =>
 {
 logging.ClearProviders();
 logging.AddDebug();
 logging.AddConsole();
 logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.GetDefaultLogPath()));
 })
 .ConfigureServices(services =>
 {
 services.AddSingleton<IDeviceManager, DeviceManager>();
 services.AddSingleton<PluginLoader>();

 services.AddSingleton<MainWindowViewModel>();
 services.AddSingleton<MainWindow>();

 services.AddFlasherFeature();
 })
 .Build();

 await _host.StartAsync();

 var deviceManager = _host.Services.GetRequiredService<IDeviceManager>();
 await deviceManager.ScanPluginsAsync();

 var window = _host.Services.GetRequiredService<MainWindow>();
 window.Show();
 }

 protected override async void OnExit(ExitEventArgs e)
 {
 if (_host is not null)
 {
 var deviceManager = _host.Services.GetRequiredService<IDeviceManager>();
 await deviceManager.ShutdownAsync();
 await _host.StopAsync();
 _host.Dispose();
 }

 base.OnExit(e);
 }
}
