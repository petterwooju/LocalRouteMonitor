namespace LocalRouteMonitor;

public sealed class SpeedTestStatus
{
    public string Name { get; set; } = string.Empty;
    public string PathType { get; set; } = string.Empty;
    public string TargetRegion { get; set; } = string.Empty;
    public string PublicIp { get; set; } = "-";
    public string PublicCountry { get; set; } = "-";
    public string Latency { get; set; } = "-";
    public string Tcp443 { get; set; } = "-";
    public string DownloadEstimate { get; set; } = "待实现";
    public string UploadEstimate { get; set; } = "待实现";
    public string Summary { get; set; } = string.Empty;
}

public sealed class VpnStatus
{
    public string CurrentVpn { get; set; } = "未识别";
    public string DefaultRoute { get; set; } = "-";
    public string PublicIp { get; set; } = "-";
    public string PublicCountry { get; set; } = "-";
    public string LocalPublicIp { get; set; } = "-";
    public string LocalPublicCountry { get; set; } = "-";
    public string VpnPublicIp { get; set; } = "-";
    public string VpnPublicCountry { get; set; } = "-";
    public string Summary { get; set; } = "-";
}
