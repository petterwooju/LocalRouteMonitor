using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalRouteMonitor;

public static class NetworkInspector
{
    private sealed record PublicIpInfo(string Ip, string Country);

    public static VpnStatus CheckVpnStatus()
    {
        var status = new VpnStatus();
        var adapters = RunPowerShell("Get-NetAdapter | Where-Object {$_.Status -eq 'Up'} | Select-Object Name,Status | ConvertTo-Csv -NoTypeInformation");
        var adapterText = string.Join("\n", adapters);
        if (adapterText.Contains("LetsTAP") || adapterText.Contains("LetsVPN", StringComparison.OrdinalIgnoreCase))
            status.CurrentVpn = "LetsVPN";
        else if (adapterText.Contains("01.lynx", StringComparison.OrdinalIgnoreCase) || adapterText.Contains("Lynx", StringComparison.OrdinalIgnoreCase))
            status.CurrentVpn = "01.lynx";
        else
            status.CurrentVpn = "未识别 / 未连接";

        var defaultRoute = RunPowerShell("route print 0.0.0.0 | findstr /R \"0.0.0.0\"")
            .FirstOrDefault(x => x.Contains("0.0.0.0"));
        status.DefaultRoute = string.IsNullOrWhiteSpace(defaultRoute) ? "-" : Regex.Replace(defaultRoute.Trim(), "\\s+", " ");

        var localInfo = TryGetPublicIpForPath("local");
        var vpnInfo = TryGetPublicIpForPath("vpn");

        status.LocalPublicIp = localInfo.Ip;
        status.LocalPublicCountry = localInfo.Country;
        status.VpnPublicIp = vpnInfo.Ip;
        status.VpnPublicCountry = vpnInfo.Country;
        status.PublicIp = vpnInfo.Ip != "获取失败(网络/拦截)" ? vpnInfo.Ip : localInfo.Ip;
        status.PublicCountry = vpnInfo.Ip != "获取失败(网络/拦截)" ? vpnInfo.Country : localInfo.Country;
        status.Summary = $"当前 VPN: {status.CurrentVpn}；VPN IP: {status.VpnPublicIp}（{status.VpnPublicCountry}）；非 VPN IP: {status.LocalPublicIp}（{status.LocalPublicCountry}）";
        return status;
    }

    public static List<SpeedTestStatus> CheckSpeedModes()
    {
        return
        [
            CheckMode(
                name: "非 VPN 速度（speedtest.cn 场景）",
                pathType: "非 VPN",
                targetRegion: "中国国内 / speedtest.cn 场景",
                host: "www.speedtest.cn",
                port: 443,
                downloadUrl: "https://mirrors.aliyun.com/ubuntu/ls-lR.gz",
                uploadUrl: "https://speed.cloudflare.com/__up",
                publicIpPath: "local"),
            CheckMode(
                name: "VPN 速度（fast.com 场景）",
                pathType: "VPN",
                targetRegion: "海外 / fast.com 场景",
                host: "fast.com",
                port: 443,
                downloadUrl: "https://speed.cloudflare.com/__down?bytes=8000000",
                uploadUrl: "https://speed.cloudflare.com/__up",
                publicIpPath: "vpn")
        ];
    }

    public static List<VpnProbeStatus> CheckVpnProbeMatrix()
    {
        return
        [
            ProbeTarget("Telegram API", "TCP 443", "api.telegram.org", 443),
            ProbeTarget("Cloudflare Speed", "TCP 443", "speed.cloudflare.com", 443),
            ProbeTarget("Google", "TCP 443", "www.google.com", 443),
            ProbeTarget("GitHub", "TCP 443", "github.com", 443)
        ];
    }

    private static SpeedTestStatus CheckMode(string name, string pathType, string targetRegion, string host, int port, string downloadUrl, string? uploadUrl, string publicIpPath)
    {
        var publicInfo = TryGetPublicIpForPath(publicIpPath);
        var status = new SpeedTestStatus
        {
            Name = name,
            PathType = pathType,
            TargetRegion = targetRegion,
            PublicIp = publicInfo.Ip,
            PublicCountry = publicInfo.Country
        };

        status.Tcp443 = CheckTcp(host, port) ? "成功" : "失败";
        status.Latency = MeasureTcpLatency(host, port);
        status.DownloadEstimate = MeasureDownloadMbps(downloadUrl);
        status.UploadEstimate = string.IsNullOrWhiteSpace(uploadUrl)
            ? "上传测速目标：待接入"
            : MeasureUploadMbps(uploadUrl);
        status.Summary = $"{pathType} / {targetRegion}；当前公网 IP {status.PublicIp}（综合地区：{status.PublicCountry}）；当前为近似场景测速，后续可再升级为真实站点测速。";
        return status;
    }

    private static PublicIpInfo TryGetPublicIpForPath(string pathType)
    {
        var endpoints = new (string Url, string Mode)[]
        {
            ("https://api.ipify.org?format=json", "json_ip"),
            ("https://ip.sb", "text"),
            ("https://ifconfig.me/ip", "text")
        };

        foreach (var ep in endpoints)
        {
            try
            {
                using var http = CreateHttpClientForPath(pathType, 6);
                var text = http.GetStringAsync(ep.Url).GetAwaiter().GetResult().Trim();
                string? ip = null;

                if (ep.Mode == "json_ip")
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("ip", out var ipEl))
                        ip = ipEl.GetString();
                }
                else if (Regex.IsMatch(text, "^\\d+\\.\\d+\\.\\d+\\.\\d+$"))
                {
                    ip = text;
                }

                if (!string.IsNullOrWhiteSpace(ip))
                    return new PublicIpInfo(ip, LookupCountryByIp(ip));
            }
            catch (TaskCanceledException)
            {
            }
            catch
            {
            }
        }

        return new PublicIpInfo("获取失败(网络/拦截)", "未知");
    }

    private static string LookupCountryByIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Contains("失败", StringComparison.OrdinalIgnoreCase))
            return "未知";

        var candidates = new List<string>();
        foreach (var candidate in QueryCountryCandidates(ip))
        {
            var normalized = NormalizeCountryName(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
                candidates.Add(normalized);
        }

        if (candidates.Count == 0)
            return "未知";

        var winner = candidates
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => CountryPriority(g.Key))
            .Select(g => g.Key)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(winner) ? candidates[0] : winner;
    }

    private static IEnumerable<string> QueryCountryCandidates(string ip)
    {
        var results = new List<string>();
        var endpoints = new[]
        {
            $"https://api.ip.sb/geoip/{ip}",
            $"https://ipwho.is/{ip}",
            $"http://ip-api.com/json/{ip}?fields=country,countryCode,regionName,city,status,message",
            $"https://ipinfo.io/{ip}/json"
        };

        foreach (var url in endpoints)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LocalRouteMonitor/1.0");
                var json = http.GetStringAsync(url).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("country", out var countryEl))
                {
                    var country = countryEl.GetString();
                    if (!string.IsNullOrWhiteSpace(country)) results.Add(country);
                }

                if (root.TryGetProperty("country_name", out var countryNameEl))
                {
                    var countryName = countryNameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(countryName)) results.Add(countryName);
                }

                if (root.TryGetProperty("countryCode", out var countryCodeEl))
                {
                    var countryCode = countryCodeEl.GetString();
                    if (!string.IsNullOrWhiteSpace(countryCode)) results.Add(countryCode);
                }

                if (root.TryGetProperty("regionName", out var regionNameEl))
                {
                    var regionName = regionNameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(regionName) && regionName.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase))
                        results.Add("Hong Kong");
                }

                if (root.TryGetProperty("region", out var regionEl))
                {
                    var region = regionEl.GetString();
                    if (!string.IsNullOrWhiteSpace(region) && region.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase))
                        results.Add("Hong Kong");
                }

                if (root.TryGetProperty("city", out var cityEl))
                {
                    var city = cityEl.GetString();
                    if (!string.IsNullOrWhiteSpace(city) && city.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase))
                        results.Add("Hong Kong");
                }
            }
            catch
            {
            }
        }

        return results;
    }

    private static string NormalizeCountryName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var value = raw.Trim();

        if (value.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Hong Kong", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("香港", StringComparison.OrdinalIgnoreCase))
            return "Hong Kong";

        if (value.Equals("SC", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Seychelles", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Seychelles", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("塞舌尔", StringComparison.OrdinalIgnoreCase))
            return "Seychelles";

        if (value.Equals("CN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("China", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("中国", StringComparison.OrdinalIgnoreCase))
            return "China";

        return value;
    }

    private static int CountryPriority(string country)
    {
        if (country.Equals("Hong Kong", StringComparison.OrdinalIgnoreCase)) return 0;
        if (country.Equals("China", StringComparison.OrdinalIgnoreCase)) return 1;
        if (country.Equals("Seychelles", StringComparison.OrdinalIgnoreCase)) return 9;
        return 5;
    }

    private static HttpClient CreateHttpClientForPath(string pathType, int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseProxy = false
        };

        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var bindAddress = pathType.Equals("vpn", StringComparison.OrdinalIgnoreCase)
                    ? GetVpnBindAddress()
                    : GetLocalBindAddress();

                if (!string.IsNullOrWhiteSpace(bindAddress) && System.Net.IPAddress.TryParse(bindAddress, out var localIp))
                    socket.Bind(new System.Net.IPEndPoint(localIp, 0));

                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LocalRouteMonitor/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }

    private static string GetLocalBindAddress()
    {
        var lines = RunPowerShell("Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -match 'WLAN|Wi-Fi|以太网|Ethernet' -and $_.IPAddress -notlike '169.254*' } | Sort-Object InterfaceMetric,SkipAsSource | Select-Object -ExpandProperty IPAddress");
        return lines.FirstOrDefault(ip => Regex.IsMatch(ip ?? string.Empty, "^(192\\.168|10\\.|172\\.)")) ?? string.Empty;
    }

    private static string GetVpnBindAddress()
    {
        var lines = RunPowerShell("Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -match 'Lets|Lynx|TAP' } | Select-Object -ExpandProperty IPAddress");
        return lines.FirstOrDefault(ip => Regex.IsMatch(ip ?? string.Empty, "^26\\.")) ?? string.Empty;
    }

    private static VpnProbeStatus ProbeTarget(string target, string protocol, string host, int port)
    {
        var ok = CheckTcp(host, port);
        var status = new VpnProbeStatus
        {
            Target = target,
            Protocol = protocol,
            Result = ok ? "成功" : "失败",
            Notes = ok ? $"{host}:{port} 可达" : $"{host}:{port} 不可达或超时"
        };

        if (ok)
        {
            var samples = MeasureTcpLatencySamples(host, port, attempts: 4, timeoutMs: 2000);
            if (samples.Count > 0)
            {
                status.LatencyHistory.AddRange(samples);
                status.Latency = $"{samples.Average():F0} ms";
            }
            else
            {
                status.Latency = "超时";
            }
        }
        else
        {
            status.Latency = "不可达";
        }

        return status;
    }

    private static string MeasureDownloadMbps(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LocalRouteMonitor/1.0");
            var sw = Stopwatch.StartNew();
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using var stream = resp.Content.ReadAsStream();
            var buffer = new byte[64 * 1024];
            long total = 0;
            const int maxBytes = 4 * 1024 * 1024;
            while (total < maxBytes)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - (int)total));
                if (read <= 0) break;
                total += read;
            }
            sw.Stop();
            if (sw.Elapsed.TotalSeconds <= 0 || total <= 0) return "失败";
            var mbps = (total * 8d / 1_000_000d) / sw.Elapsed.TotalSeconds;
            return $"{mbps:F1} Mbps";
        }
        catch (TaskCanceledException)
        {
            return "超时";
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue) return $"失败({(int)ex.StatusCode.Value})";
            return "失败(请求)";
        }
        catch
        {
            return "失败";
        }
    }

    private static string MeasureUploadMbps(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LocalRouteMonitor/1.0");
            var payload = new byte[1024 * 1024];
            Random.Shared.NextBytes(payload);
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var sw = Stopwatch.StartNew();
            using var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
            sw.Stop();
            resp.EnsureSuccessStatusCode();
            var mbps = (payload.Length * 8d / 1_000_000d) / sw.Elapsed.TotalSeconds;
            return $"{mbps:F1} Mbps";
        }
        catch (TaskCanceledException)
        {
            return "超时";
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue) return $"失败({(int)ex.StatusCode.Value})";
            return "失败(请求)";
        }
        catch
        {
            return "失败";
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

    private static List<long> MeasureTcpLatencySamples(string host, int port, int attempts = 4, int timeoutMs = 2000)
    {
        var result = new List<long>();
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var client = new TcpClient();
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(host, port);
                if (!task.Wait(timeoutMs)) continue;
                sw.Stop();
                if (client.Connected) result.Add(sw.ElapsedMilliseconds);
            }
            catch
            {
            }
        }
        return result;
    }

    private static bool CheckTcp(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(2000) && client.Connected;
        }
        catch
        {
            return false;
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
        var text = p.StandardOutput.ReadToEnd() + Environment.NewLine + p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
