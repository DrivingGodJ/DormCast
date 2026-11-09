using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    readonly Grid _root = new() { Padding = new Thickness(28, 4, 28, 20), RowSpacing = 16, RequestedTheme = ElementTheme.Default };
    readonly TextBlock _state = Label("未广播", 14), _detail = Label("连接同一个 Wi-Fi，把此刻的音乐分享出去。", 14), _song = Label("你的音乐，正在这里。", 30), _artist = Label("等待播放器提供歌曲信息", 18), _album = Label("", 14), _capture = Label("开启广播后采集系统声音", 13), _network = Label("检查局域网…", 13), _listeners = Label("0 台设备", 22), _sync = Label("等待设备测量", 22), _speaker = Label("正在连接实际扬声器", 13), _percent = Label("—", 16);
    readonly StackPanel _clientCards = new() { Spacing = 12 };
    readonly Image _cover = new() { Width = 190, Height = 190, Stretch = Stretch.UniformToFill }, _qr = new() { Width = 184, Height = 184, Stretch = Stretch.Uniform };
    readonly TextBlock _coverFallback = Label("◉", 66);
    readonly TextBox _name = new() { Header = "广播名称" }, _url = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 56, Text = "开始后显示可收藏的固定网址" };
    readonly ComboBox _nic = new() { Header = "局域网卡", HorizontalAlignment = HorizontalAlignment.Stretch }, _frame = new() { Header = "音频帧", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly NumberBox _port = new() { Header = "端口", Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    readonly Slider _slider = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, IsEnabled = false };
    readonly Button _start = ActionButton("开始广播", true), _copy = ActionButton("复制固定链接"), _copyIp = ActionButton("IP 备用"), _settings = ActionButton("同步设置"), _refresh = ActionButton("刷新网卡"), _update = ActionButton("检查更新");
    static SolidColorBrush Brush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));
    static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static StackPanel Stack(params UIElement[] children) { var panel = new StackPanel { Spacing = 12 }; foreach (var child in children) panel.Children.Add(child); return panel; }
    static Button ActionButton(string text, bool primary = false) => new() { Content = text, MinHeight = 40, Padding = new Thickness(16, 8, 16, 8), Background = primary ? Brush(201, 243, 127) : Brush(40, 48, 41), Foreground = primary ? Brush(19, 32, 14) : Brush(236, 243, 231), CornerRadius = new CornerRadius(8) };
    static Border Card(UIElement child) => new() { Tag = "card", Child = child, Padding = new Thickness(24), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16) };
    static Grid Columns(params double[] stars) { var grid = new Grid { ColumnSpacing = 20 }; foreach (var star in stars) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(star, GridUnitType.Star) }); return grid; }
    static void Cell(Grid grid, FrameworkElement child, int column) { Grid.SetColumn(child, column); grid.Children.Add(child); }
    void BuildLayout()
    {
        _root.Background = new SolidColorBrush(Colors.Transparent);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titlebar = new Grid { Height = 32, Background = new SolidColorBrush(Colors.Transparent), Margin = new Thickness(-28, 0, -28, 0), Padding = new Thickness(28, 0, 140, 0) };
        titlebar.Children.Add(Label("宿舍之音  ·  DormCast", 12)); _root.Children.Add(titlebar); ExtendsContentIntoTitleBar = true; SetTitleBar(titlebar);
        var header = Columns(1, 1); Cell(header, Stack(Label("宿舍之音", 30), Label("DORMCAST  /  局域网音乐广播", 12)), 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var exit = ActionButton("退出程序"); exit.Click += async (_, _) => await ExitAsync(); actions.Children.Add(_state); actions.Children.Add(_settings); actions.Children.Add(exit); Cell(header, actions, 1); Grid.SetRow(header, 1); _root.Children.Add(header);
        var content = new StackPanel { Spacing = 20 }; var hero = Columns(1.7, 1);
        var volumeHeader = Columns(1, 0.2); Cell(volumeHeader, Label("系统与广播音量", 18), 0); _percent.HorizontalAlignment = HorizontalAlignment.Right; Cell(volumeHeader, _percent, 1); AutomationProperties.SetName(_slider, "系统与广播统一音量");
        var coverBox = new Grid { Width = 190, Height = 190, Background = Brush(38, 49, 40), CornerRadius = new CornerRadius(12) }; _coverFallback.HorizontalAlignment = HorizontalAlignment.Center; coverBox.Children.Add(_coverFallback); coverBox.Children.Add(_cover);
        var music = new Grid { ColumnSpacing = 24 }; music.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); music.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); Cell(music, coverBox, 0); _song.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; Cell(music, Stack(Label("主机正在播放", 12), _song, _artist, _album), 1); Cell(hero, Card(Stack(music, _capture, volumeHeader, _slider, _speaker)), 0);
        var shareButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; shareButtons.Children.Add(_copy); shareButtons.Children.Add(_copyIp);
        Cell(hero, Card(Stack(Label("扫码，一起听", 20), new Border { Child = _qr, Background = Brush(255, 255, 255), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center }, _url, shareButtons)), 1); content.Children.Add(hero);
        var config = Columns(1.2, 1.5, 0.7, 1.05); Cell(config, _name, 0); Cell(config, _nic, 1); Cell(config, _port, 2); Cell(config, _frame, 3);
        var startRow = Columns(1, 1); Cell(startRow, Stack(_network, _refresh), 0); _start.HorizontalAlignment = HorizontalAlignment.Stretch; _start.FontSize = 18; _start.MinHeight = 56; Cell(startRow, _start, 1); content.Children.Add(Card(Stack(config, startRow)));
        var metrics = Columns(1, 1); Cell(metrics, Stack(Label("正在收听", 13), _listeners), 0); Cell(metrics, Stack(Label("统一延迟目标", 13), _sync), 1); content.Children.Add(Card(metrics));
        var diagnostics = ActionButton("导出诊断"); diagnostics.Click += async (_, _) => await SaveDiagnosticsAsync(); content.Children.Add(new Expander { Header = "设备与同步详情", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(_clientCards, Label("基础路径、播放时间线和偏差均为软件估计；浏览器输出值不一定包含后台媒体和蓝牙的额外缓冲。", 12), diagnostics) }); content.Children.Add(_update);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); _root.Children.Add(scroll); Grid.SetRow(_detail, 3); _root.Children.Add(_detail);
        _root.SizeChanged += (_, _) => { var size = _root.ActualWidth < 1000 ? 140 : 190; if (_cover.Width != size) _cover.Width = _cover.Height = coverBox.Width = coverBox.Height = size; };
    }
}
