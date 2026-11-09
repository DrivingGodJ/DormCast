using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DormCast.Desktop;

sealed record ClientDetail(string Id, string Title, string Freshness, string[] Values)
{
    public static readonly string[] Labels = ["基础路径估计", "当前主机补偿", "补偿计划值", "统一目标", "推算播放偏差", "浏览器播放时间线", "网络 RTT", "网络抖动", "接收缓冲", "区间丢包率", "浏览器基础缓冲", "输出延迟报告", "额外硬件校准", "校时 RTT / 不确定度", "已发送 / 丢弃帧"];
    public static ClientDetail[] Read(object[] clients, double target) => clients.Select(value =>
    {
        var row = JsonSerializer.SerializeToElement(value); var metrics = row.GetProperty("metrics");
        static double? Number(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) ? n : null;
        static string Ms(double? number) => number is { } n ? $"{n:0.0} ms" : "—";
        var baseline = Number(row, "baselineMs"); var hold = Number(row, "sendDelayMs"); var age = Number(row, "measurementAgeMs"); var metricAge = Number(row, "metricsAgeMs");
        var hidden = row.TryGetProperty("background", out var background) && background.GetBoolean();
        var stale = age is null || age > 5000;
        var loss = Number(metrics, "lossPercent"); var correction = Number(metrics, "hardwareCorrectionMs");
        var text = stale ? age is { } a ? $"基础测量已 {a / 1000:0} 秒未更新，补偿仍由主机执行" : "等待有效播放时间线测量" : $"基础测量更新于 {age / 1000:0.0} 秒前";
        text += metricAge is { } ma ? $"；网络数据更新于 {ma / 1000:0.0} 秒前" : "；等待网络数据";
        var error = baseline is { } b && hold is { } h ? (double?)(b + h - target) : null;
        return new ClientDetail(row.GetProperty("id").GetString()!, $"{row.GetProperty("name").GetString()} · {row.GetProperty("state").GetString()} · {(hidden ? "后台" : "前台")}", text,
            [Ms(baseline), Ms(hold), Ms(Number(row, "plannedSendDelayMs")), Ms(target), error is { } e ? $"{e:+0.0;-0.0;0.0} ms{(stale ? "（旧测量推算）" : "")}" : "—", Ms(Number(metrics, "naturalMs")), Ms(Number(metrics, "rttMs")), Ms(Number(metrics, "jitterMs")), Ms(Number(metrics, "bufferMs")), loss is { } l ? $"{l:0.00}%" : "—", Ms(Number(metrics, "browserBaseLatencyMs")), Ms(Number(metrics, "outputLatencyMs")), Ms(correction), $"{Ms(Number(metrics, "clockRttMs"))} / ±{Ms(Number(metrics, "clockUncertaintyMs"))}", $"{Number(row, "sentFrames"):0} / {Number(row, "droppedFrames"):0}"]);
    }).ToArray();
}

public sealed partial class MainWindow
{
    readonly Dictionary<string, (StackPanel panel, TextBlock title, TextBlock freshness, TextBlock[] values)> _devicePanels = new();
    void PresentClients(ClientDetail[] clients)
    {
        foreach (var missing in _devicePanels.Keys.Except(clients.Select(c => c.Id)).ToArray()) { _clientCards.Children.Remove(_devicePanels[missing].panel); _devicePanels.Remove(missing); }
        foreach (var client in clients)
        {
            if (!_devicePanels.TryGetValue(client.Id, out var view))
            {
                var title = Label(client.Title, 16); var freshness = Label(client.Freshness, 12); var grid = new Grid { ColumnSpacing = 16, RowSpacing = 14 };
                for (var i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var values = ClientDetail.Labels.Select(_ => Label("—", 15)).ToArray();
                for (var i = 0; i < values.Length; i++) { var cell = Stack(Label(ClientDetail.Labels[i], 12), values[i]); Grid.SetColumn(cell, i % 5); Grid.SetRow(cell, i / 5); grid.Children.Add(cell); }
                var panel = Stack(title, freshness, grid); panel.Margin = new Thickness(0, 0, 0, 20); view = (panel, title, freshness, values); _devicePanels.Add(client.Id, view); _clientCards.Children.Add(panel);
            }
            SetText(view.title, client.Title); SetText(view.freshness, client.Freshness);
            for (var i = 0; i < view.values.Length; i++) SetText(view.values[i], client.Values[i]);
        }
    }
}
