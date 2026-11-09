using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    readonly UISettings _appearance = new();
    string? _themeKey;
    void InitializeTheme()
    {
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _root.ActualThemeChanged += (_, _) => ApplyTheme(); _root.Loaded += (_, _) => ApplyTheme();
        _appearance.ColorValuesChanged += AppearanceChanged; ApplyTheme();
    }
    void AppearanceChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyTheme);
    void ApplyTheme()
    {
        if (_exiting) return;
        var dark = _root.ActualTheme == ElementTheme.Dark;
        var text = dark ? ColorHelper.FromArgb(255, 238, 242, 238) : ColorHelper.FromArgb(255, 28, 32, 29);
        var accent = _appearance.GetColorValue(UIColorType.Accent);
        var key = $"{dark}:{accent}"; if (key == _themeKey) return; _themeKey = key;
        static double Linear(byte channel) { var value = channel / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        var luminance = Linear(accent.R) * 0.2126 + Linear(accent.G) * 0.7152 + Linear(accent.B) * 0.0722;
        var onAccent = luminance > 0.179 ? Colors.Black : Colors.White;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent; AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = text; AppWindow.TitleBar.ButtonInactiveForegroundColor = text;
        void Theme(UIElement element)
        {
            if (element is TextBlock label) label.Foreground = new SolidColorBrush(text);
            if (element is Button button)
            {
                button.Background = new SolidColorBrush(ReferenceEquals(button, _start) ? accent : dark ? ColorHelper.FromArgb(160, 64, 68, 65) : ColorHelper.FromArgb(185, 255, 255, 255));
                button.Foreground = new SolidColorBrush(ReferenceEquals(button, _start) ? onAccent : text);
            }
            if (element is Border border)
            {
                if (Equals(border.Tag, "card")) { border.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(165, 32, 37, 33) : ColorHelper.FromArgb(185, 255, 255, 255)); border.BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(110, 106, 119, 108) : ColorHelper.FromArgb(70, 80, 96, 83)); }
                if (border.Child != null) Theme(border.Child);
            }
            if (element is Panel panel) foreach (var child in panel.Children) Theme(child);
            else if (element is ScrollViewer scroll && scroll.Content is UIElement scrollChild) Theme(scrollChild);
            else if (element is ContentControl content && content is not Button && content.Content is UIElement child) Theme(child);
        }
        Theme(_root);
    }
}
