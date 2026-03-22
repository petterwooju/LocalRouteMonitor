using System.Collections.ObjectModel;

namespace LocalRouteMonitor;

public sealed class AppRouteStatus
{
    public string AppName { get; set; } = string.Empty;
    public string Status { get; set; } = "未检测";
    public string Summary { get; set; } = "未开始";
    public string SourceAddress { get; set; } = "-";
    public string RemoteAddress { get; set; } = "-";
    public string RemotePort { get; set; } = "-";
    public string InterfaceAlias { get; set; } = "-";
    public string Gateway { get; set; } = "-";
    public string RouteMatched { get; set; } = "-";
    public string RouteCoverage { get; set; } = "-";
    public string ProcessCount { get; set; } = "-";
    public string ConnectionStates { get; set; } = "-";
    public string DetectedRemoteIps { get; set; } = "-";
    public string UnmatchedRemoteIps { get; set; } = "-";
    public string UnmatchedCount { get; set; } = "0";
    public string UnmatchedLabel { get; set; } = "未命中IP";
    public string RepairHint { get; set; } = "-";
    public string RepairHintForeground { get; set; } = "#667085";
    public string RecommendedActionTitle { get; set; } = "修复指向";
    public string RefreshAdvice { get; set; } = "是否建议再次刷新：待判断";
    public string ManualRouteAdvice { get; set; } = "是否建议手工补路由：待判断";
    public string AdviceForeground { get; set; } = "#667085";
    public string AdviceBackground { get; set; } = "#F8FAFC";
    public string UnmatchedForeground { get; set; } = "#B42318";
    public string UnmatchedBackground { get; set; } = "#FFF1F3";
    public string Latency { get; set; } = "-";
    public string TcpReachable { get; set; } = "-";
    public string StatusBadgeBackground { get; set; } = "#EAF3FF";
    public string StatusBadgeForeground { get; set; } = "#1F5FBF";
    public string CardAccentBrush { get; set; } = "#D8E0EF";
    public string CardBackground { get; set; } = "#FFFFFF";
}

public sealed class MainViewModel : NotifyBase
{
    private string _lastChecked = "尚未检测";
    private string _overallSummary = "准备就绪，等待检测";
    private string _refreshSummary = "尚未执行路由刷新";
    private string _refreshResultTitle = "刷新结果摘要";
    private string _refreshResultSummary = "完成检测后，会在这里汇总新增 IP、未命中应用和稳定走本地的应用。";
    private string _refreshResultUnmatched = "未命中应用：-";
    private string _refreshResultStable = "稳定本地出口：-";
    private string _refreshDeltaSummary = "尚未执行闭环刷新。";
    private string _routeRefreshDetail = "尚未执行路由刷新。";
    private string _adminModeSummary = "管理员模式：检测中...";
    private string _adminModeForeground = "#667085";
    private string _adminModeBackground = "#F8FAFC";
    private VpnStatus _vpn = new();
    public ObservableCollection<AppRouteStatus> Items { get; } = new();
    public ObservableCollection<SpeedTestStatus> SpeedItems { get; } = new();
    public ObservableCollection<VpnProbeStatus> VpnProbeItems { get; } = new();
    public string LastChecked { get => _lastChecked; set => Set(ref _lastChecked, value); }
    public string OverallSummary { get => _overallSummary; set => Set(ref _overallSummary, value); }
    public string RefreshSummary { get => _refreshSummary; set => Set(ref _refreshSummary, value); }
    public string RefreshResultTitle { get => _refreshResultTitle; set => Set(ref _refreshResultTitle, value); }
    public string RefreshResultSummary { get => _refreshResultSummary; set => Set(ref _refreshResultSummary, value); }
    public string RefreshResultUnmatched { get => _refreshResultUnmatched; set => Set(ref _refreshResultUnmatched, value); }
    public string RefreshResultStable { get => _refreshResultStable; set => Set(ref _refreshResultStable, value); }
    public string RefreshDeltaSummary { get => _refreshDeltaSummary; set => Set(ref _refreshDeltaSummary, value); }
    public string RouteRefreshDetail { get => _routeRefreshDetail; set => Set(ref _routeRefreshDetail, value); }
    public string AdminModeSummary { get => _adminModeSummary; set => Set(ref _adminModeSummary, value); }
    public string AdminModeForeground { get => _adminModeForeground; set => Set(ref _adminModeForeground, value); }
    public string AdminModeBackground { get => _adminModeBackground; set => Set(ref _adminModeBackground, value); }
    public VpnStatus Vpn { get => _vpn; set => Set(ref _vpn, value); }
}
