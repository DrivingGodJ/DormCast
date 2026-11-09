using QRCoder;

namespace DormCast.Desktop;

public sealed partial class MainWindow
{
    // Both server addresses stay active; this selection controls only presentation.
    bool _useIpv4Join;
    string? DisplayedJoinUrl => _useIpv4Join ? _host.IpJoinUrl : _host.JoinUrl;
    async Task RefreshJoinAsync()
    {
        var url = DisplayedJoinUrl;
        _url.Text = url ?? "开始后显示加入网址"; _qr.Source = null;
        _joinHint.Text = _useIpv4Join
            ? "IPv4 直接连接，不需要主机名解析。主机 IP 变化后请重新扫码。两种方式同时有效。"
            : "固定链接可以收藏。手机打不开时可选 IPv4 兼容。两种方式同时有效。";
        if (url == null) return;
        using var code = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(code);
        var image = await DecodeAsync(png.GetGraphic(6), 368);
        // A stop or another selection can complete while image decoding yields to the UI.
        if (_host.Running && DisplayedJoinUrl == url) _qr.Source = image;
    }
    async Task SwitchJoinModeAsync(bool useIpv4)
    {
        // SelectionChanged can precede SelectedIndex updating. Use its AddedItems value.
        _useIpv4Join = useIpv4;
        if (!_host.Running || _busy) return;
        try { await RefreshJoinAsync(); }
        catch (Exception ex) { _detail.Text = "二维码更新失败，可复制当前链接加入。"; DormCast.Hosting.AppLog.Write("Join QR update", ex); }
    }
}
