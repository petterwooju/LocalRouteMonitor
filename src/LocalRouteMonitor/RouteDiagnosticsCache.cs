using System.IO;
using System.Text.Json;

namespace LocalRouteMonitor;

public sealed class RouteDiagnosticsCacheEntry
{
    public string AppName { get; set; } = string.Empty;
    public List<string> DetectedRemoteIps { get; set; } = new();
    public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
}

public static class RouteDiagnosticsCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalRouteMonitor",
        "route_diagnostics_cache.json");

    public static void Save(IEnumerable<AppRouteStatus> items)
    {
        var previous = Load()
            .ToDictionary(x => x.AppName, StringComparer.OrdinalIgnoreCase);

        var entries = items.Select(x =>
        {
            var currentIps = SplitIps(x.DetectedRemoteIps);
            if (previous.TryGetValue(x.AppName, out var old))
            {
                currentIps = currentIps
                    .Concat(old.DetectedRemoteIps ?? new List<string>())
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .ToList();
            }

            return new RouteDiagnosticsCacheEntry
            {
                AppName = x.AppName,
                DetectedRemoteIps = currentIps,
                LastCheckedUtc = DateTime.UtcNow,
            };
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<RouteDiagnosticsCacheEntry> Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return Array.Empty<RouteDiagnosticsCacheEntry>();
            var text = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<List<RouteDiagnosticsCacheEntry>>(text) ?? new List<RouteDiagnosticsCacheEntry>();
        }
        catch
        {
            return Array.Empty<RouteDiagnosticsCacheEntry>();
        }
    }

    private static List<string> SplitIps(string raw)
        => (raw ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x != "-")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
