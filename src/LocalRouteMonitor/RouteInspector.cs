using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LocalRouteMonitor;

public static class RouteInspector
{
    private static readonly string[] PreferredStates =
    [
        "Established",
        "SynSent",
        "SynReceived",
        "CloseWait",
        "FinWait1",
        "FinWait2",
        "LastAck",
        "TimeWait"
    ];

    public static List<AppRouteStatus> CheckAll()
    {
        return
        [
            CheckApp("TeamViewer", new[] { "TeamViewer", "TeamViewer_Service", "TeamViewer_Desktop" }),
            CheckApp("飞书 / Feishu", new[] { "Feishu", "Lark" }),
            CheckApp("百度网盘 / Baidu Netdisk", new[] { "BaiduNetdisk", "baidunetdiskhost", "BaiduNetdiskUnite" }),
            CheckOpenClawTelegram()
        ];
    }

    private static AppRouteStatus CheckApp(string displayName, string[] processNames)
    {
        var status = new AppRouteStatus { AppName = displayName };
        var pids = Process.GetProcesses()
            .Where(p => processNames.Any(name => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Id)
            .ToHashSet();

        status.ProcessCount = pids.Count.ToString();

        if (pids.Count == 0)
        {
            status.Status = "未运行";
            status.Summary = "未发现相关进程";
            status.ConnectionStates = "-";
            status.DetectedRemoteIps = "-";
            status.RouteCoverage = "-";
            status.UnmatchedLabel = "未命中IP（未检测）";
            status.RecommendedActionTitle = "修复指向";
            status.RefreshAdvice = "是否建议再次刷新：否，先启动应用后再检测";
            status.ManualRouteAdvice = "是否建议手工补路由：否";
            ApplyBadgeStyle(status);
            return status;
        }

        var connLines = RunPowerShell(@"Get-NetTCPConnection | Select-Object State,LocalAddress,LocalPort,RemoteAddress,RemotePort,OwningProcess | ConvertTo-Csv -NoTypeInformation");
        var candidates = new List<RouteCandidate>();
        foreach (var line in connLines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("State") || line.StartsWith("\"State\"")) continue;
            var parts = ParseCsv(line);
            if (parts.Count < 6) continue;
            if (!int.TryParse(parts[5], out var pid) || !pids.Contains(pid)) continue;
            var state = parts[0];
            if (!PreferredStates.Contains(state, StringComparer.OrdinalIgnoreCase)) continue;
            if (!Regex.IsMatch(parts[3], "^\\d+\\.\\d+\\.\\d+\\.\\d+$")) continue;
            if (parts[3].StartsWith("127.")) continue;
            candidates.Add(new RouteCandidate(parts[1], parts[3], parts[4], pid, state));
        }

        if (candidates.Count == 0)
        {
            status.Status = "未连接";
            status.Summary = "进程存在，但未发现可用于判定出口的对外连接";
            status.ConnectionStates = "无候选连接";
            status.DetectedRemoteIps = "-";
            status.RouteCoverage = "0/0";
            status.UnmatchedLabel = "未命中IP（暂无连接）";
            status.RecommendedActionTitle = "修复指向";
            status.RefreshAdvice = "是否建议再次刷新：是，等应用建立连接后重试";
            status.ManualRouteAdvice = "是否建议手工补路由：否，先让工具捕获真实远端 IP";
            ApplyBadgeStyle(status);
            return status;
        }

        candidates = candidates
            .Distinct()
            .ToList();

        var routeMap = candidates
            .Select(c => new { Candidate = c, Route = DetectRoute(c.Remote) })
            .ToDictionary(
                x => BuildCandidateKey(x.Candidate),
                x => x.Route,
                StringComparer.OrdinalIgnoreCase);

        var chosen = candidates
            .OrderBy(c => GetConnectionPriority(c, routeMap))
            .ThenBy(c => StatePriority(c.State))
            .ThenBy(c => c.Remote)
            .First();

        status.SourceAddress = chosen.Local;
        status.RemoteAddress = chosen.Remote;
        status.RemotePort = chosen.Port;
        status.ConnectionStates = string.Join(", ", candidates.Select(c => c.State).Distinct(StringComparer.OrdinalIgnoreCase));

        var uniqueRemotes = candidates.Select(c => c.Remote).Distinct().OrderBy(x => x).ToList();
        status.DetectedRemoteIps = string.Join(Environment.NewLine, uniqueRemotes);

        var routeByRemote = routeMap
            .GroupBy(x => x.Key.Split('|')[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Value).FirstOrDefault(r => r.Matched) != default
                    ? g.Select(x => x.Value).First(r => r.Matched)
                    : g.First().Value,
                StringComparer.OrdinalIgnoreCase);

        var matchedRemotes = new List<string>();
        var unmatchedRemotes = new List<string>();
        foreach (var remote in uniqueRemotes)
        {
            var route = routeByRemote.GetValueOrDefault(remote);
            if (route.Matched && IsLocalGateway(route.Gateway))
                matchedRemotes.Add(remote);
            else
                unmatchedRemotes.Add(remote);
        }

        status.RouteCoverage = $"{matchedRemotes.Count}/{uniqueRemotes.Count}";
        status.UnmatchedRemoteIps = unmatchedRemotes.Count == 0
            ? "无"
            : string.Join(Environment.NewLine, unmatchedRemotes);
        status.UnmatchedCount = unmatchedRemotes.Count.ToString();
        status.UnmatchedLabel = unmatchedRemotes.Count == 0
            ? "未命中IP（0）"
            : $"未命中IP（{unmatchedRemotes.Count}）";

        var chosenRoute = routeMap[BuildCandidateKey(chosen)];
        if (chosenRoute.Matched)
        {
            status.RouteMatched = "是";
            status.Gateway = chosenRoute.Gateway;
            status.InterfaceAlias = chosenRoute.InterfaceAddress;
        }
        else
        {
            status.RouteMatched = "否";
            status.Gateway = "-";
            status.InterfaceAlias = "-";
        }

        try
        {
            using var ping = new Ping();
            var replies = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var reply = ping.Send(chosen.Remote, 1200);
                if (reply?.Status == IPStatus.Success) replies.Add(reply.RoundtripTime);
            }
            status.Latency = replies.Count > 0 ? $"{replies.Average():F0} ms" : "超时";
        }
        catch
        {
            status.Latency = "失败";
        }

        status.TcpReachable = CheckTcp(chosen.Remote, int.TryParse(chosen.Port, out var p) ? p : 443) ? "成功" : "失败";

        var localLike = IsLocalAddress(chosen.Local);
        var vpnLike = IsVpnLikeAddress(chosen.Local);
        var routeOk = matchedRemotes.Contains(chosen.Remote);
        var allRoutesMatched = uniqueRemotes.Count > 0 && matchedRemotes.Count == uniqueRemotes.Count;
        var hasLocalCandidate = candidates.Any(c => IsLocalAddress(c.Local));
        var hasLocalMatchedCandidate = candidates.Any(c =>
        {
            var route = routeMap.GetValueOrDefault(BuildCandidateKey(c));
            return IsLocalAddress(c.Local) && route.Matched && IsLocalGateway(route.Gateway);
        });

        if (routeOk && localLike)
        {
            status.Status = "本地出口";
            status.Summary = $"候选远端 {uniqueRemotes.Count} 个；当前主判定连接 {chosen.Local} -> {chosen.Remote}:{chosen.Port}，命中本地网关 {status.Gateway}";
            status.RepairHint = "状态良好，无需额外处理";
            status.RepairHintForeground = "#177245";
            status.RefreshAdvice = unmatchedRemotes.Count > 0 ? "是否建议再次刷新：可选，若想补齐剩余未命中可刷新一次" : "是否建议再次刷新：否";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：可选，仅对剩余未命中 IP 处理" : "是否建议手工补路由：否";
        }
        else if (allRoutesMatched && hasLocalCandidate && hasLocalMatchedCandidate)
        {
            status.Status = "本地出口";
            status.Summary = $"当前仍观测到 VPN 源连接 {chosen.Local} -> {chosen.Remote}:{chosen.Port}，但 {uniqueRemotes.Count}/{uniqueRemotes.Count} 个候选远端已全部命中本地路由，且已存在本地源地址候选，判定应用已恢复本地出口";
            status.RepairHint = "路由已全部命中本地出口；若想让主连接展示也切到本地，可重启应用后复测";
            status.RepairHintForeground = "#177245";
            status.RefreshAdvice = "是否建议再次刷新：否，当前路由已全部命中";
            status.ManualRouteAdvice = "是否建议手工补路由：否";
        }
        else if (vpnLike)
        {
            status.Status = "VPN出口";
            status.Summary = $"当前主判定连接 {chosen.Local} -> {chosen.Remote}:{chosen.Port}；检测到 {uniqueRemotes.Count} 个候选远端，其中 {matchedRemotes.Count} 个已命中本地路由";
            status.RepairHint = unmatchedRemotes.Count > 0 ? "建议再次刷新路由；若仍失败，请补未命中 IP 静态路由" : "建议再次检测确认主连接是否切换";
            status.RepairHintForeground = "#B42318";
            status.RefreshAdvice = "是否建议再次刷新：是，优先执行";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：是，若刷新后仍未命中" : "是否建议手工补路由：暂不需要，先复测主连接";
        }
        else
        {
            status.Status = "待确认";
            status.Summary = $"检测到 {uniqueRemotes.Count} 个候选远端；其中 {matchedRemotes.Count} 个命中本地路由，{unmatchedRemotes.Count} 个未命中";
            status.RepairHint = unmatchedRemotes.Count > 0 ? "当前已走本地或混合出口，但仍建议补齐未命中 IP" : "建议再检测一次确认是否稳定";
            status.RepairHintForeground = "#B25E09";
            status.RefreshAdvice = unmatchedRemotes.Count > 0 ? "是否建议再次刷新：是，建议先刷新再复测" : "是否建议再次刷新：是，再检测一次确认稳定性";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：视刷新结果决定，若仍未命中再补" : "是否建议手工补路由：否";
        }

        ApplyBadgeStyle(status);
        return status;
    }

    private static AppRouteStatus CheckOpenClawTelegram()
    {
        var telegramIps = ResolveTelegramIpv4Targets();
        var pids = GetOpenClawNodePids();

        var status = new AppRouteStatus
        {
            AppName = "OpenClaw / Telegram API",
            RecommendedActionTitle = "VPN 修复指向"
        };

        status.ProcessCount = pids.Count.ToString();

        if (pids.Count == 0)
        {
            status.Status = "未运行";
            status.Summary = "未发现 openclaw.mjs 对应的 node 进程";
            status.ConnectionStates = "-";
            status.DetectedRemoteIps = telegramIps.Count == 0 ? "-" : string.Join(Environment.NewLine, telegramIps);
            status.RouteCoverage = "0/0";
            status.UnmatchedLabel = "未命中IP（未检测）";
            status.RefreshAdvice = "是否建议再次刷新：否，先确认 OpenClaw 正在运行";
            status.ManualRouteAdvice = "是否建议手工补路由：否，先捕获真实 Telegram API 连接";
            ApplyBadgeStyle(status);
            return status;
        }

        var connLines = RunPowerShell(@"Get-NetTCPConnection | Select-Object State,LocalAddress,LocalPort,RemoteAddress,RemotePort,OwningProcess | ConvertTo-Csv -NoTypeInformation");
        var candidates = new List<RouteCandidate>();
        foreach (var line in connLines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("State") || line.StartsWith("\"State\"")) continue;
            var parts = ParseCsv(line);
            if (parts.Count < 6) continue;
            if (!int.TryParse(parts[5], out var pid) || !pids.Contains(pid)) continue;
            var state = parts[0];
            if (!PreferredStates.Contains(state, StringComparer.OrdinalIgnoreCase)) continue;
            if (!Regex.IsMatch(parts[3], "^\\d+\\.\\d+\\.\\d+\\.\\d+$")) continue;
            if (parts[3].StartsWith("127.")) continue;
            if (parts[4] != "443") continue;
            if (telegramIps.Count > 0 && !telegramIps.Contains(parts[3], StringComparer.OrdinalIgnoreCase)) continue;
            candidates.Add(new RouteCandidate(parts[1], parts[3], parts[4], pid, state));
        }

        if (candidates.Count == 0)
        {
            status.Status = "未连接";
            status.Summary = telegramIps.Count == 0
                ? "OpenClaw 进程存在，但当前未解析到 Telegram IPv4 或未捕获到 Telegram API 连接"
                : "OpenClaw 进程存在，但当前未捕获到 Telegram API 的 TCP 443 连接";
            status.ConnectionStates = "无候选连接";
            status.DetectedRemoteIps = telegramIps.Count == 0 ? "-" : string.Join(Environment.NewLine, telegramIps);
            status.RouteCoverage = $"0/{telegramIps.Count}";
            status.UnmatchedRemoteIps = telegramIps.Count == 0 ? "-" : string.Join(Environment.NewLine, telegramIps);
            status.UnmatchedCount = telegramIps.Count.ToString();
            status.UnmatchedLabel = telegramIps.Count == 0 ? "未命中IP（DNS未解析）" : $"未命中IP（{telegramIps.Count}）";
            status.RepairHint = "建议在 OpenClaw 正在收发 Telegram 消息时再检测一次，以捕获真实 API 连接";
            status.RepairHintForeground = "#B25E09";
            status.RefreshAdvice = "是否建议再次刷新：是，在 Telegram 活跃时重试";
            status.ManualRouteAdvice = "是否建议手工补路由：暂不建议，先拿到真实连接 IP";
            ApplyBadgeStyle(status);
            return status;
        }

        candidates = candidates.Distinct().ToList();

        var routeMap = candidates
            .Select(c => new { Candidate = c, Route = DetectRoute(c.Remote) })
            .ToDictionary(
                x => BuildCandidateKey(x.Candidate),
                x => x.Route,
                StringComparer.OrdinalIgnoreCase);

        var chosen = candidates
            .OrderBy(c => GetTelegramConnectionPriority(c, routeMap))
            .ThenBy(c => StatePriority(c.State))
            .ThenBy(c => c.Remote)
            .First();

        status.SourceAddress = chosen.Local;
        status.RemoteAddress = chosen.Remote;
        status.RemotePort = chosen.Port;
        status.ConnectionStates = string.Join(", ", candidates.Select(c => c.State).Distinct(StringComparer.OrdinalIgnoreCase));

        var uniqueRemotes = candidates.Select(c => c.Remote).Distinct().OrderBy(x => x).ToList();
        status.DetectedRemoteIps = string.Join(Environment.NewLine, uniqueRemotes);

        var routeByRemote = routeMap
            .GroupBy(x => x.Key.Split('|')[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Value).FirstOrDefault(r => r.Matched) != default
                    ? g.Select(x => x.Value).First(r => r.Matched)
                    : g.First().Value,
                StringComparer.OrdinalIgnoreCase);

        var matchedRemotes = new List<string>();
        var unmatchedRemotes = new List<string>();
        foreach (var remote in uniqueRemotes)
        {
            var route = routeByRemote.GetValueOrDefault(remote);
            if (route.Matched && IsVpnLikeAddress(route.InterfaceAddress))
                matchedRemotes.Add(remote);
            else
                unmatchedRemotes.Add(remote);
        }

        status.RouteCoverage = $"{matchedRemotes.Count}/{uniqueRemotes.Count}";
        status.UnmatchedRemoteIps = unmatchedRemotes.Count == 0 ? "无" : string.Join(Environment.NewLine, unmatchedRemotes);
        status.UnmatchedCount = unmatchedRemotes.Count.ToString();
        status.UnmatchedLabel = unmatchedRemotes.Count == 0 ? "未命中IP（0）" : $"未命中IP（{unmatchedRemotes.Count}）";

        var chosenRoute = routeMap[BuildCandidateKey(chosen)];
        if (chosenRoute.Matched)
        {
            status.RouteMatched = "是";
            status.Gateway = chosenRoute.Gateway;
            status.InterfaceAlias = chosenRoute.InterfaceAddress;
        }
        else
        {
            status.RouteMatched = "否";
            status.Gateway = "-";
            status.InterfaceAlias = "-";
        }

        status.Latency = MeasureTcpLatency(chosen.Remote, 443);
        status.TcpReachable = CheckTcp(chosen.Remote, 443) ? "成功" : "失败";

        var vpnLike = IsVpnLikeAddress(chosen.Local) || IsVpnLikeAddress(status.InterfaceAlias);
        var routeOk = matchedRemotes.Contains(chosen.Remote);
        var allRoutesMatched = uniqueRemotes.Count > 0 && matchedRemotes.Count == uniqueRemotes.Count;

        if (routeOk && vpnLike)
        {
            status.Status = "VPN出口";
            status.Summary = $"当前主判定连接 {chosen.Local} -> {chosen.Remote}:{chosen.Port}，已命中 VPN 路由 {status.Gateway}";
            status.RepairHint = "状态良好，Telegram API 当前已稳定走 VPN";
            status.RepairHintForeground = "#177245";
            status.RefreshAdvice = unmatchedRemotes.Count > 0 ? "是否建议再次刷新：可选，若想补齐剩余 Telegram IP 可继续扩展 VPN 路由" : "是否建议再次刷新：否";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：可选，仅针对剩余 Telegram IP" : "是否建议手工补路由：否";
        }
        else if (allRoutesMatched)
        {
            status.Status = "VPN出口";
            status.Summary = $"已捕获 {uniqueRemotes.Count}/{uniqueRemotes.Count} 个 Telegram API 远端并全部命中 VPN 路由；当前主连接显示为 {chosen.Local}";
            status.RepairHint = "候选 Telegram 连接已全部命中 VPN；若主连接显示不理想，可继续观察是否切换";
            status.RepairHintForeground = "#177245";
            status.RefreshAdvice = "是否建议再次刷新：否，当前候选连接已全部命中";
            status.ManualRouteAdvice = "是否建议手工补路由：否";
        }
        else if (IsLocalAddress(chosen.Local) || IsLocalGateway(status.Gateway))
        {
            status.Status = "本地出口";
            status.Summary = $"当前主判定连接 {chosen.Local} -> {chosen.Remote}:{chosen.Port} 更像走本地出口；这可能导致 Telegram API 在 VPN 场景下不稳定";
            status.RepairHint = "建议后续补上 Telegram API -> VPN 定向路由，避免链路混走";
            status.RepairHintForeground = "#B42318";
            status.RefreshAdvice = "是否建议再次刷新：是，建议尽快增加 VPN 定向刷新能力";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：是，优先把 Telegram API 远端 IP 钉到 VPN 网关" : "是否建议手工补路由：是，可尝试手工固定 Telegram API 路由";
        }
        else
        {
            status.Status = "待确认";
            status.Summary = $"已捕获 {uniqueRemotes.Count} 个 Telegram API 候选远端，其中 {matchedRemotes.Count} 个命中 VPN 路由，{unmatchedRemotes.Count} 个未命中";
            status.RepairHint = "当前看起来存在混合出口；需要继续把 Telegram API 连接固定到 VPN";
            status.RepairHintForeground = "#B25E09";
            status.RefreshAdvice = "是否建议再次刷新：是，建议增加 Telegram VPN 刷新逻辑后复测";
            status.ManualRouteAdvice = unmatchedRemotes.Count > 0 ? "是否建议手工补路由：是，优先处理未命中的 Telegram IP" : "是否建议手工补路由：视复测结果决定";
        }

        ApplyBadgeStyle(status);
        return status;
    }

    private static int StatePriority(string state)
    {
        var idx = Array.FindIndex(PreferredStates, s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : PreferredStates.Length + 1;
    }

    private static (int RouteRank, int LocalRank, int VpnRank) GetConnectionPriority(
        RouteCandidate candidate,
        IReadOnlyDictionary<string, (bool Matched, string Gateway, string InterfaceAddress)> routeMap)
    {
        var route = routeMap.GetValueOrDefault(BuildCandidateKey(candidate));
        var localLike = IsLocalAddress(candidate.Local);
        var routeMatchedLocal = route.Matched && IsLocalGateway(route.Gateway);
        var vpnLike = IsVpnLikeAddress(candidate.Local);

        return (
            RouteRank: routeMatchedLocal ? 0 : 1,
            LocalRank: localLike ? 0 : 1,
            VpnRank: vpnLike ? 1 : 0);
    }

    private static (int RouteRank, int VpnRank, int LocalRank) GetTelegramConnectionPriority(
        RouteCandidate candidate,
        IReadOnlyDictionary<string, (bool Matched, string Gateway, string InterfaceAddress)> routeMap)
    {
        var route = routeMap.GetValueOrDefault(BuildCandidateKey(candidate));
        var routeMatchedVpn = route.Matched && IsVpnLikeAddress(route.InterfaceAddress);
        var vpnLike = IsVpnLikeAddress(candidate.Local);
        var localLike = IsLocalAddress(candidate.Local);

        return (
            RouteRank: routeMatchedVpn ? 0 : 1,
            VpnRank: vpnLike ? 0 : 1,
            LocalRank: localLike ? 1 : 0);
    }

    private static string BuildCandidateKey(RouteCandidate candidate)
        => $"{candidate.Local}|{candidate.Remote}|{candidate.Port}|{candidate.Pid}|{candidate.State}";

    private static bool IsLocalAddress(string address)
        => address.StartsWith("192.168.") || address.StartsWith("10.") || address.StartsWith("172.");

    private static bool IsVpnLikeAddress(string address)
        => address.StartsWith("26.");

    private static bool IsLocalGateway(string gateway)
        => gateway.StartsWith("192.168.") || gateway.StartsWith("10.") || gateway.StartsWith("172.");

    private static HashSet<int> GetOpenClawNodePids()
    {
        var result = new HashSet<int>();

        try
        {
            var lines = RunPowerShell("Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'node.exe' } | Select-Object ProcessId,CommandLine | ConvertTo-Csv -NoTypeInformation");
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("ProcessId") || line.StartsWith("\"ProcessId\"")) continue;
                var parts = ParseCsv(line);
                if (parts.Count < 2) continue;
                if (!int.TryParse(parts[0], out var pid)) continue;
                var commandLine = parts[1] ?? string.Empty;
                if (commandLine.Contains("openclaw.mjs", StringComparison.OrdinalIgnoreCase))
                    result.Add(pid);
            }
        }
        catch
        {
            // ignore and fallback to empty set
        }

        return result;
    }

    private static HashSet<string> ResolveTelegramIpv4Targets()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var address in Dns.GetHostAddresses("api.telegram.org"))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(address.ToString());
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    private static (bool Matched, string Gateway, string InterfaceAddress) DetectRoute(string remote)
    {
        var routeLine = RunPowerShell($"route print {remote} | findstr /R \"{remote}\"")
            .FirstOrDefault(x => x.Contains(remote, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(routeLine))
            return (false, string.Empty, string.Empty);

        var tokens = Regex.Split(routeLine.Trim(), "\\s+");
        if (tokens.Length >= 4)
            return (true, tokens[2], tokens[3]);
        return (true, string.Empty, string.Empty);
    }

    private static void ApplyBadgeStyle(AppRouteStatus status)
    {
        status.UnmatchedForeground = status.UnmatchedCount == "0" ? "#177245" : "#B42318";
        status.UnmatchedBackground = status.UnmatchedCount == "0" ? "#ECFDF3" : "#FFF1F3";

        if (status.Status == "本地出口")
        {
            status.StatusBadgeBackground = "#EAF7EE";
            status.StatusBadgeForeground = "#177245";
            status.CardAccentBrush = "#177245";
            status.CardBackground = "#F6FEF9";
            status.AdviceForeground = "#177245";
            status.AdviceBackground = "#ECFDF3";
            return;
        }

        if (status.Status == "未运行" || status.Status == "未连接")
        {
            status.StatusBadgeBackground = "#F3F4F6";
            status.StatusBadgeForeground = "#667085";
            status.CardAccentBrush = "#D0D5DD";
            status.CardBackground = "#FCFCFD";
            status.AdviceForeground = "#667085";
            status.AdviceBackground = "#F8FAFC";
            return;
        }

        if (status.Status == "VPN出口")
        {
            status.StatusBadgeBackground = "#FEE4E2";
            status.StatusBadgeForeground = "#B42318";
            status.CardAccentBrush = "#F04438";
            status.CardBackground = "#FFFBFA";
            status.AdviceForeground = "#B42318";
            status.AdviceBackground = "#FEF3F2";
            return;
        }

        status.StatusBadgeBackground = "#FFF4E5";
        status.StatusBadgeForeground = "#B25E09";
        status.CardAccentBrush = "#F79009";
        status.CardBackground = "#FFFCF5";
        status.AdviceForeground = "#B25E09";
        status.AdviceBackground = "#FFF7ED";
    }

    private static bool CheckTcp(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(1500) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string MeasureTcpLatency(string host, int port, int timeoutMs = 2000)
    {
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs)) return "超时";
            sw.Stop();
            return client.Connected ? $"{sw.ElapsedMilliseconds} ms" : "失败";
        }
        catch
        {
            return "失败";
        }
    }

    private static List<string> RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var current = string.Empty;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes)
            {
                result.Add(current);
                current = string.Empty;
                continue;
            }
            current += ch;
        }
        result.Add(current);
        return result;
    }

    private sealed record RouteCandidate(string Local, string Remote, string Port, int Pid, string State);
}
