using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalRouteMonitor;

public static class RouteRefresher
{
    public sealed class RefreshResult
    {
        public int TargetCount { get; set; }
        public int LocalTargetCount { get; set; }
        public int VpnTargetCount { get; set; }
        public List<string> Verified { get; set; } = new();
        public List<string> AlreadyExists { get; set; } = new();
        public List<string> RequiresElevation { get; set; } = new();
        public List<string> VerificationFailed { get; set; } = new();
        public List<string> OtherFailures { get; set; } = new();
        public string SummaryText { get; set; } = string.Empty;
    }

    private sealed record RefreshPlan(string Destination, string Mask, string VerifyIp, string Gateway, string StrategyLabel);

    private static readonly string[] AllowedStates =
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

    private static readonly string[] SeedTargets =
    [
        "8.130.28.143",
        "8.130.100.90",
        "34.97.66.5",
        "34.97.66.6",
        "37.252.244.135",
        "47.108.208.17",
        "188.172.203.38"
    ];

    private static readonly (string Destination, string Mask, string VerifyIp)[] TelegramSeedRoutes =
    [
        ("149.154.160.0", "255.255.240.0", "149.154.166.110"),
        ("91.108.4.0", "255.255.252.0", "91.108.4.1"),
        ("91.108.8.0", "255.255.252.0", "91.108.8.1"),
        ("91.108.12.0", "255.255.252.0", "91.108.12.1"),
        ("91.108.16.0", "255.255.252.0", "91.108.16.1"),
        ("91.108.56.0", "255.255.252.0", "91.108.56.1")
    ];

    public static RefreshResult RefreshKnownRoutes()
    {
        var localGateway = DetectLocalGateway();
        if (string.IsNullOrWhiteSpace(localGateway))
            return new RefreshResult { SummaryText = "未检测到本地默认网关，未执行路由刷新。" };

        var vpnGateway = DetectVpnGateway();
        var localTargets = CollectLocalPriorityTargets();
        var vpnTargets = CollectTelegramVpnTargets();

        var plans = new Dictionary<string, RefreshPlan>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in localTargets)
            plans[$"{ip}/32"] = new RefreshPlan(ip, "255.255.255.255", ip, localGateway, "本地优先");

        foreach (var ip in vpnTargets)
        {
            if (!string.IsNullOrWhiteSpace(vpnGateway))
                plans[$"{ip}/32"] = new RefreshPlan(ip, "255.255.255.255", ip, vpnGateway, "Telegram走VPN");
        }

        if (!string.IsNullOrWhiteSpace(vpnGateway))
        {
            foreach (var route in TelegramSeedRoutes)
                plans[$"{route.Destination}/{route.Mask}"] = new RefreshPlan(route.Destination, route.Mask, route.VerifyIp, vpnGateway, "Telegram网段走VPN");
        }

        if (plans.Count == 0)
            return new RefreshResult { SummaryText = "未收集到可刷新的远端 IP，未执行路由刷新。" };

        var result = new RefreshResult
        {
            TargetCount = plans.Count,
            LocalTargetCount = plans.Values.Count(x => x.StrategyLabel == "本地优先"),
            VpnTargetCount = plans.Values.Count(x => x.StrategyLabel.StartsWith("Telegram", StringComparison.OrdinalIgnoreCase))
        };

        if (vpnTargets.Count > 0 && string.IsNullOrWhiteSpace(vpnGateway))
        {
            result.OtherFailures.Add($"Telegram API 共 {vpnTargets.Count} 个目标未刷新：未检测到 VPN 网关");
        }

        foreach (var plan in plans.Values.OrderBy(x => x.VerifyIp, StringComparer.OrdinalIgnoreCase))
        {
            _ = RunPowerShell($"route delete {plan.Destination} >$null 2>&1");
            var addResult = RunPowerShell($"route -p add {plan.Destination} mask {plan.Mask} {plan.Gateway} metric 5");
            var verify = DetectRoute(plan.VerifyIp);
            var labelTarget = plan.Mask == "255.255.255.255" ? plan.Destination : $"{plan.Destination}/{plan.Mask}";
            if (verify.Matched && string.Equals(verify.Gateway, plan.Gateway, StringComparison.OrdinalIgnoreCase))
            {
                result.Verified.Add($"[{plan.StrategyLabel}] {labelTarget} -> {plan.Gateway}");
                continue;
            }

            var addOutput = string.Join(" | ", addResult).Trim();
            if (addOutput.Contains("The object already exists", StringComparison.OrdinalIgnoreCase) ||
                addOutput.Contains("对象已存在", StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyExists.Add($"[{plan.StrategyLabel}] {labelTarget}");
                continue;
            }

            if (addOutput.Contains("requires elevation", StringComparison.OrdinalIgnoreCase) ||
                addOutput.Contains("需要提升", StringComparison.OrdinalIgnoreCase) ||
                addOutput.Contains("请求的操作需要提升", StringComparison.OrdinalIgnoreCase))
            {
                result.RequiresElevation.Add($"[{plan.StrategyLabel}] {labelTarget}");
                continue;
            }

            if (verify.Matched)
            {
                result.VerificationFailed.Add($"[{plan.StrategyLabel}] {labelTarget} (当前网关 {verify.Gateway})");
                continue;
            }

            result.OtherFailures.Add(string.IsNullOrWhiteSpace(addOutput)
                ? $"[{plan.StrategyLabel}] {labelTarget}"
                : $"[{plan.StrategyLabel}] {labelTarget} ({addOutput})");
        }

        var sb = new StringBuilder();
        var failureCount = result.RequiresElevation.Count + result.VerificationFailed.Count + result.OtherFailures.Count;
        sb.Append($"已尝试刷新 {result.TargetCount} 条目标 IP（本地优先 {result.LocalTargetCount}，Telegram走VPN {result.VpnTargetCount}）；成功验证 {result.Verified.Count} 条");
        if (result.AlreadyExists.Count > 0) sb.Append($"，已存在 {result.AlreadyExists.Count} 条");
        if (failureCount > 0) sb.Append($"，失败 {failureCount} 条");
        sb.Append('。');

        if (result.Verified.Count > 0)
            sb.Append($" 已验证：{string.Join("; ", result.Verified.Take(12))}");
        if (result.RequiresElevation.Count > 0)
            sb.Append($" 需管理员权限：{string.Join("; ", result.RequiresElevation.Take(8))}");
        if (result.VerificationFailed.Count > 0)
            sb.Append($" 验证失败：{string.Join("; ", result.VerificationFailed.Take(6))}");
        if (result.OtherFailures.Count > 0)
            sb.Append($" 其他失败：{string.Join("; ", result.OtherFailures.Take(6))}");
        if (result.AlreadyExists.Count > 0)
            sb.Append($" 已存在：{string.Join("; ", result.AlreadyExists.Take(6))}");

        result.SummaryText = sb.ToString();
        return result;
    }

    private static HashSet<string> CollectLocalPriorityTargets()
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in DetectAppRemoteIps(new[] { "Feishu", "Lark" })) targets.Add(ip);
        foreach (var ip in DetectAppRemoteIps(new[] { "TeamViewer", "TeamViewer_Service", "TeamViewer_Desktop" })) targets.Add(ip);
        foreach (var ip in DetectAppRemoteIps(new[] { "BaiduNetdisk", "baidunetdiskhost", "BaiduNetdiskUnite" })) targets.Add(ip);
        foreach (var ip in LoadRecentDetectedIps(["飞书 / Feishu", "TeamViewer", "百度网盘 / Baidu Netdisk"])) targets.Add(ip);
        foreach (var ip in SeedTargets) targets.Add(ip);

        return targets.Where(IsValidIpv4).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CollectTelegramVpnTargets()
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in ResolveTelegramIpv4Targets()) targets.Add(ip);
        foreach (var ip in LoadRecentDetectedIps(["OpenClaw / Telegram API"])) targets.Add(ip);
        foreach (var ip in DetectOpenClawTelegramRemoteIps()) targets.Add(ip);

        return targets.Where(IsValidIpv4).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string DetectLocalGateway()
    {
        var lines = RunPowerShell("route print 0.0.0.0");
        foreach (var line in lines)
        {
            if (!line.Contains("0.0.0.0")) continue;
            var parts = Regex.Split(line.Trim(), "\\s+");
            if (parts.Length >= 3 && parts[0] == "0.0.0.0" && parts[1] == "0.0.0.0")
            {
                var gw = parts[2];
                if (gw != "On-link" && IsLocalAddress(gw))
                    return gw;
            }
        }
        return string.Empty;
    }

    private static string DetectVpnGateway()
    {
        var lines = RunPowerShell("Get-NetRoute -AddressFamily IPv4 | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/1','128.0.0.0/1') -and $_.InterfaceAlias -match 'Lets|Lynx|TAP' } | Sort-Object RouteMetric | Select-Object -ExpandProperty NextHop");
        foreach (var line in lines)
        {
            var gateway = line.Trim();
            if (IsValidIpv4(gateway))
                return gateway;
        }
        return string.Empty;
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

    private static bool IsValidIpv4(string? value)
        => Regex.IsMatch(value ?? string.Empty, "^\\d+\\.\\d+\\.\\d+\\.\\d+$") && !(value ?? string.Empty).StartsWith("127.");

    private static bool IsLocalAddress(string value)
        => value.StartsWith("192.168.") || value.StartsWith("10.") || value.StartsWith("172.");

    private static IEnumerable<string> ResolveTelegramIpv4Targets()
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses("api.telegram.org");
        }
        catch
        {
            return Array.Empty<string>();
        }

        return addresses
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> DetectOpenClawTelegramRemoteIps()
    {
        var telegramIps = ResolveTelegramIpv4Targets().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (telegramIps.Count == 0)
            yield break;

        var csv = RunPowerShell("Get-NetTCPConnection | Select-Object State,RemoteAddress,RemotePort,OwningProcess | ConvertTo-Csv -NoTypeInformation");
        var pids = GetOpenClawNodePids();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in csv)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("State") || line.StartsWith("\"State\"")) continue;
            var parts = ParseCsv(line);
            if (parts.Count < 4) continue;
            if (!int.TryParse(parts[3], out var pid) || !pids.Contains(pid)) continue;
            if (!AllowedStates.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
            if (parts[2] != "443") continue;
            if (!telegramIps.Contains(parts[1])) continue;
            if (!IsValidIpv4(parts[1])) continue;
            if (seen.Add(parts[1]))
                yield return parts[1];
        }
    }

    private static HashSet<int> GetOpenClawNodePids()
    {
        var result = new HashSet<int>();
        var lines = RunPowerShell("Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'node.exe' } | Select-Object ProcessId,CommandLine | ConvertTo-Csv -NoTypeInformation");
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("ProcessId") || line.StartsWith("\"ProcessId\"")) continue;
            var parts = ParseCsv(line);
            if (parts.Count < 2) continue;
            if (!int.TryParse(parts[0], out var pid)) continue;
            if ((parts[1] ?? string.Empty).Contains("openclaw.mjs", StringComparison.OrdinalIgnoreCase))
                result.Add(pid);
        }
        return result;
    }

    private static IEnumerable<string> DetectAppRemoteIps(string[] processNames)
    {
        var csv = RunPowerShell("Get-NetTCPConnection | Select-Object State,RemoteAddress,OwningProcess | ConvertTo-Csv -NoTypeInformation");
        var pids = Process.GetProcesses()
            .Where(p => processNames.Any(name => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Id)
            .ToHashSet();

        var seen = new HashSet<string>();
        foreach (var line in csv)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("State") || line.StartsWith("\"State\"")) continue;
            var parts = ParseCsv(line);
            if (parts.Count < 3) continue;
            if (!int.TryParse(parts[2], out var pid) || !pids.Contains(pid)) continue;
            if (!AllowedStates.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
            if (!Regex.IsMatch(parts[1], "^\\d+\\.\\d+\\.\\d+\\.\\d+$")) continue;
            if (parts[1].StartsWith("127.")) continue;
            if (seen.Add(parts[1]))
                yield return parts[1];
        }
    }

    private static IEnumerable<string> LoadRecentDetectedIps(IEnumerable<string> appNames)
    {
        var cutoff = DateTime.UtcNow.AddHours(-12);
        var nameSet = appNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in RouteDiagnosticsCache.Load())
        {
            if (entry.LastCheckedUtc < cutoff) continue;
            if (!nameSet.Contains(entry.AppName)) continue;
            foreach (var ip in entry.DetectedRemoteIps)
            {
                if (Regex.IsMatch(ip, "^\\d+\\.\\d+\\.\\d+\\.\\d+$") && !ip.StartsWith("127."))
                    yield return ip;
            }
        }
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
        var text = p.StandardOutput.ReadToEnd() + Environment.NewLine + p.StandardError.ReadToEnd();
        p.WaitForExit(8000);
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
