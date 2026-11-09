using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    void VolumeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_host.Running || _busy || _exiting || !_slider.IsEnabled || e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        // Keep native editing/selection keys, and avoid applying Slider's own key step twice.
        for (var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject; focused != null; focused = VisualTreeHelper.GetParent(focused))
            if (focused is TextBox or PasswordBox or NumberBox or ComboBox or Slider or ListViewBase) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        _slider.Value = Math.Clamp(_slider.Value + (e.Key == VirtualKey.Up ? 2 : -2), 0, 100);
        e.Handled = true;
    }
}
