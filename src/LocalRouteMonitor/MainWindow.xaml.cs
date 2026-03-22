using System.Collections.Generic;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace LocalRouteMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _isRefreshing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await RefreshDataAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshDataAsync();

    private async void RefreshRoutesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            _vm.LastChecked = "闭环刷新中...";
            _vm.OverallSummary = "正在执行：先检测、再刷新路由、再自动复测，请稍候...";

            var before = await Task.Run(CaptureSnapshot);
            RouteDiagnosticsCache.Save(before.Items);

            var result = await Task.Run(RouteRefresher.RefreshKnownRoutes);

            var after = await Task.Run(CaptureSnapshot);
            RouteDiagnosticsCache.Save(after.Items);
            ApplySnapshot(after);

            _vm.RefreshSummary = BuildRefreshHeadline(result.SummaryText);
            _vm.RouteRefreshDetail = BuildRefreshDetail(result);
            _vm.RefreshDeltaSummary = BuildDeltaSummary(before.Items, after.Items);
            MessageBox.Show($"{_vm.RouteRefreshDetail}\n\n{_vm.RefreshDeltaSummary}", "刷新路由", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _vm.OverallSummary = $"闭环刷新失败：{ex.Message}";
            _vm.LastChecked = $"闭环刷新失败: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            MessageBox.Show(ex.Message, "刷新路由失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static string BuildRefreshHeadline(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "刷新路由已完成，但未返回结果明细。";

        if (detail.Contains("失败 0 条", StringComparison.OrdinalIgnoreCase))
            return "刷新路由已完成：本轮写入的目标 IP 均已通过验证。";

        if (detail.Contains("需管理员权限", StringComparison.OrdinalIgnoreCase))
            return "刷新路由已执行，但当前未以管理员模式完整写入；请查看下方分组结果。";

        if (detail.Contains("失败", StringComparison.OrdinalIgnoreCase))
            return "刷新路由已执行，但仍有部分 IP 未成功写入；请查看下方明细。";

        return "刷新路由已执行；请结合下方明细和最新检测结果确认是否生效。";
    }

    private static string BuildRefreshDetail(RouteRefresher.RefreshResult result)
    {
        var parts = new List<string>
        {
            result.SummaryText
        };

        if (result.RequiresElevation.Count > 0)
            parts.Add($"需管理员权限（{result.RequiresElevation.Count}）：{string.Join("; ", result.RequiresElevation.Take(10))}");
        if (result.AlreadyExists.Count > 0)
            parts.Add($"已存在（{result.AlreadyExists.Count}）：{string.Join("; ", result.AlreadyExists.Take(10))}");
        if (result.VerificationFailed.Count > 0)
            parts.Add($"验证失败（{result.VerificationFailed.Count}）：{string.Join("; ", result.VerificationFailed.Take(8))}");
        if (result.OtherFailures.Count > 0)
            parts.Add($"其他失败（{result.OtherFailures.Count}）：{string.Join("; ", result.OtherFailures.Take(8))}");

        return string.Join(Environment.NewLine, parts);
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshDataAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            _vm.LastChecked = "检测中...";
            _vm.OverallSummary = "正在执行网络与路由检测，请稍候...";

            var snapshot = await Task.Run(CaptureSnapshot);
            RouteDiagnosticsCache.Save(snapshot.Items);
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _vm.OverallSummary = $"检测失败：{ex.Message}";
            _vm.LastChecked = $"检测失败: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            MessageBox.Show(ex.ToString(), "检测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static SnapshotResult CaptureSnapshot()
    {
        var items = RouteInspector.CheckAll();
        var speedItems = NetworkInspector.CheckSpeedModes();
        var vpnProbeStatuses = NetworkInspector.CheckVpnProbeMatrix();
        var vpn = NetworkInspector.CheckVpnStatus();
        return new SnapshotResult(items, speedItems, vpnProbeStatuses, vpn);
    }

    private void ApplySnapshot(SnapshotResult snapshot)
    {
        _vm.Items.Clear();
        foreach (var item in snapshot.Items)
        {
            _vm.Items.Add(item);
        }

        _vm.SpeedItems.Clear();
        foreach (var item in snapshot.SpeedItems)
        {
            _vm.SpeedItems.Add(item);
        }

        _vm.VpnProbeItems.Clear();
        foreach (var status in snapshot.VpnProbeStatuses)
        {
            if (status.StabilityScore < 0)
            {
                status.Notes += " (评分不可用)";
            }
            _vm.VpnProbeItems.Add(status);
        }

        _vm.Vpn = snapshot.Vpn;

        var localCount = _vm.Items.Count(x => x.Status == "本地出口");
        var total = _vm.Items.Count;
        var vpnCount = _vm.Items.Count(x => x.Status == "VPN出口");
        var pendingCount = _vm.Items.Count(x => x.Status == "待确认");
        var unmatchedItems = _vm.Items.Where(x => x.UnmatchedCount != "0").ToList();
        var unmatchedApps = unmatchedItems.Count;
        var unmatchedIpCount = unmatchedItems.Sum(x => int.TryParse(x.UnmatchedCount, out var n) ? n : 0);
        var stableApps = _vm.Items.Where(x => x.Status == "本地出口" && x.UnmatchedCount == "0").Select(x => x.AppName).ToList();
        var stableLocalCount = stableApps.Count;
        var unmatchedNames = unmatchedApps == 0 ? "无" : string.Join("、", unmatchedItems.Select(x => x.AppName));
        var stableNames = stableLocalCount == 0 ? "无" : string.Join("、", stableApps);

        var isAdmin = IsRunningAsAdministrator();
        _vm.AdminModeSummary = isAdmin
            ? "管理员模式：已启用。刷新路由时可直接写入静态路由。"
            : "管理员模式：未启用。立即检测不受影响，但“刷新路由”可能因权限不足而部分失败。";
        _vm.AdminModeForeground = isAdmin ? "#177245" : "#B42318";
        _vm.AdminModeBackground = isAdmin ? "#ECFDF3" : "#FFF1F3";

        _vm.OverallSummary = $"已检测 {total} 个应用，其中 {localCount} 个本地出口，{vpnCount} 个 VPN 出口，{pendingCount} 个待确认，{unmatchedApps} 个仍有未命中 IP；当前 VPN 为 {_vm.Vpn.CurrentVpn}。";
        _vm.RefreshResultTitle = "刷新结果摘要";
        _vm.RefreshResultSummary = $"本次检测发现 {unmatchedIpCount} 个待补路由 IP；{unmatchedApps} 个应用仍有未命中，{stableLocalCount} 个应用已稳定走本地出口。";
        _vm.RefreshResultUnmatched = $"未命中应用：{unmatchedNames}";
        _vm.RefreshResultStable = $"稳定本地出口：{stableNames}";
        _vm.LastChecked = $"最后检测: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private static string BuildDeltaSummary(IReadOnlyList<AppRouteStatus> before, IReadOnlyList<AppRouteStatus> after)
    {
        var beforeMap = before.ToDictionary(x => x.AppName, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var totalBefore = 0;
        var totalAfter = 0;

        foreach (var current in after)
        {
            beforeMap.TryGetValue(current.AppName, out var previous);
            var beforeCount = ParseUnmatched(previous?.UnmatchedCount);
            var afterCount = ParseUnmatched(current.UnmatchedCount);
            totalBefore += beforeCount;
            totalAfter += afterCount;
            var delta = afterCount - beforeCount;
            var trend = delta < 0 ? $"下降 {Math.Abs(delta)}" : delta > 0 ? $"上升 {delta}" : "无变化";
            lines.Add($"{current.AppName}：{beforeCount} → {afterCount}（{trend}）");
        }

        var totalDelta = totalAfter - totalBefore;
        var totalTrend = totalDelta < 0 ? $"总未命中减少 {Math.Abs(totalDelta)}" : totalDelta > 0 ? $"总未命中增加 {totalDelta}" : "总未命中无变化";
        return $"闭环复测：{totalTrend}。{string.Join("；", lines)}";
    }

    private static int ParseUnmatched(string? raw)
        => int.TryParse(raw, out var n) ? n : 0;

    private sealed record SnapshotResult(
        List<AppRouteStatus> Items,
        List<SpeedTestStatus> SpeedItems,
        List<VpnProbeStatus> VpnProbeStatuses,
        VpnStatus Vpn);
}
