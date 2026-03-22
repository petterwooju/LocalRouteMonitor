namespace LocalRouteMonitor;

public sealed class VpnProbeStatus
{
    public string Target { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Result { get; set; } = "-";
    public string Latency { get; set; } = "-";
    public string Notes { get; set; } = "-";

    // New fields for scoring
    public List<long> LatencyHistory { get; set; } = new List<long>();
    public double AverageLatency => LatencyHistory.Count > 0 ? LatencyHistory.Average() : -1;
    public double LatencyVariance => LatencyHistory.Count > 1 ? CalculateVariance(LatencyHistory) : -1;
    public double StabilityScore => CalculateStabilityScore();

    private double CalculateVariance(List<long> history)
    {
        if (history.Count < 2) return -1;
        var avg = AverageLatency;
        var sumOfSquares = history.Sum(x => Math.Pow(x - avg, 2));
        return sumOfSquares / (history.Count - 1);
    }

    private double CalculateStabilityScore()
    {
        // Simple scoring: higher score for lower variance and consistent success
        // This is a placeholder and can be refined.
        if (LatencyHistory.Count < 5) return -1; // Need enough data points

        var successRate = (double)LatencyHistory.Count / 100; // Assuming 100 probes total for simplicity
        var score = (1.0 - (LatencyVariance / 1000.0)) * successRate; // Normalize variance (adjust divisor as needed)
        return Math.Max(0, Math.Min(100, score * 100)); // Scale to 0-100
    }
}

