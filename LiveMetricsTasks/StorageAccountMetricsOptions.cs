namespace LiveMetrics.LiveMetricsTasks;

public class StorageAccountMetricsOptions
{
    public int IntervalInSeconds { get; set; }
    public int CutoffThreshold { get; set; }
    public StorageAccountMetricsServiceOptionEntry[]? Accounts { get; set; }
}