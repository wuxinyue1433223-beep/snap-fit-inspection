using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ProgrammerShell.App.Services;
using ProgrammerShell.Core.Interfaces;

namespace ProgrammerShell.App;

public partial class DeviceMappingWindow : Window
{
 public IReadOnlyDictionary<int, CameraMappingSelection?> CameraIndexToSelection => _cameraIndexToSelection;

 private readonly Dictionary<int, CameraMappingSelection?> _cameraIndexToSelection = new();
 private readonly IReadOnlyDictionary<int, ICamera?> _connectedCamerasByIndex;

 public DeviceMappingWindow(Dictionary<int, CameraMappingSelection?> currentMapping, IReadOnlyDictionary<int, ICamera?> connectedCamerasByIndex)
 {
 InitializeComponent();
 _cameraIndexToSelection = currentMapping.ToDictionary(kv => kv.Key, kv => kv.Value);
 _connectedCamerasByIndex = connectedCamerasByIndex;
 RefreshDeviceList();
 ApplyCurrentMappingToCombos();
 }

 public DeviceMappingWindow(Dictionary<int, CameraMappingSelection?> currentMapping)
 : this(currentMapping, new Dictionary<int, ICamera?>())
 {
 }

 private void Refresh_Click(object sender, RoutedEventArgs e)
 {
 RefreshDeviceList();
 ApplyCurrentMappingToCombos();
 }

 private void Ok_Click(object sender, RoutedEventArgs e)
 {
 _cameraIndexToSelection[1] = SelectedDeviceSelection(Camera1Combo.SelectedItem);
 _cameraIndexToSelection[2] = SelectedDeviceSelection(Camera2Combo.SelectedItem);
 _cameraIndexToSelection[3] = null;
 _cameraIndexToSelection[4] = null;
 DialogResult = true;
 }

 private void Cancel_Click(object sender, RoutedEventArgs e)
 {
 DialogResult = false;
 }

 private void RefreshDeviceList()
 {
 // Use camera factories discovered from plugins.
 var deviceManager = ((App)Application.Current).Services.GetRequiredService<IDeviceManager>();
 var factories = CameraFactorySelector.GetCameraFactories(deviceManager);

 // Key fix: re-run discovery so hot-plugged devices are visible.
 // Keep it minimal: best-effort, swallow exceptions to avoid breaking the UI.
 foreach (var f in factories)
 {
 try
 {
 f.DiscoverAsync().GetAwaiter().GetResult();
 }
 catch
 {
 // ignored
 }
 }

 Camera1Combo.ItemsSource = BuildItemsForCamera(1, factories);
 Camera2Combo.ItemsSource = BuildItemsForCamera(2, factories);

 Camera1Combo.SelectedIndex = 0;
 Camera2Combo.SelectedIndex = 0;
 }

 private IEnumerable<DeviceItem> BuildItemsForCamera(int camIndex, IReadOnlyList<IDeviceFactory> factories)
 {
  var items = new List<DeviceItem> { new(-1, null, string.Empty, "<None>") };
 _connectedCamerasByIndex.TryGetValue(camIndex, out var camera);
 var currentSelection = _cameraIndexToSelection.GetValueOrDefault(camIndex);

  for (int i =0; i < factories.Count; i++)
 {
 var f = factories[i];
 if (f is IDeviceFactoryWithSelection selectable)
 {
 var devices = selectable.GetAvailableDevices();
 if (devices.Count >0)
 {
 foreach (var device in devices)
 {
 var display = $"{f.DisplayName} ({f.DeviceType}) - {device.DisplayName} [{device.DeviceId}]";
      items.Add(new DeviceItem(i, device.DeviceId, f.DeviceType, display));
 }
 continue;
 }
 }

 var fallbackId = currentSelection?.FactoryIndex == i ? currentSelection?.DeviceId : null;
 if (string.IsNullOrWhiteSpace(fallbackId))
 fallbackId = camera?.DeviceId;

 var fallbackDisplay = string.IsNullOrWhiteSpace(fallbackId)
 ? $"{f.DisplayName} ({f.DeviceType})"
 : $"{f.DisplayName} ({f.DeviceType}) - ID: {fallbackId}";

  items.Add(new DeviceItem(i, fallbackId, f.DeviceType, fallbackDisplay));
 }

 return items;
 }

 private void ApplyCurrentMappingToCombos()
 {
 SetSelectedByDeviceSelection(Camera1Combo, _cameraIndexToSelection.GetValueOrDefault(1));
 SetSelectedByDeviceSelection(Camera2Combo, _cameraIndexToSelection.GetValueOrDefault(2));
 }

 private static CameraMappingSelection? SelectedDeviceSelection(object? selectedItem)
 {
 if (selectedItem is not DeviceItem d)
 return null;
  return d.FactoryIndex <0 ? null : new CameraMappingSelection(d.FactoryIndex, d.DeviceId, d.FactoryDeviceType);
 }

 private static void SetSelectedByDeviceSelection(System.Windows.Controls.ComboBox combo, CameraMappingSelection? selection)
 {
 if (combo.ItemsSource is not IEnumerable<DeviceItem> items)
 return;

  DeviceItem? target = null;
  if (!string.IsNullOrWhiteSpace(selection?.FactoryDeviceType))
  {
    target = items.FirstOrDefault(i => string.Equals(i.FactoryDeviceType, selection.FactoryDeviceType, StringComparison.OrdinalIgnoreCase)
      && string.Equals(i.DeviceId, selection.DeviceId, StringComparison.OrdinalIgnoreCase));
  }

  target ??= items.FirstOrDefault(i => i.FactoryIndex == (selection?.FactoryIndex ?? -1)
    && string.Equals(i.DeviceId, selection?.DeviceId, StringComparison.OrdinalIgnoreCase));
 combo.SelectedItem = target;
 }

 private sealed record DeviceItem(int FactoryIndex, string? DeviceId, string FactoryDeviceType, string Display)
 {
 public override string ToString() => Display;
 }
}
