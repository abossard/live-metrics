namespace LiveMetrics.LiveMetricsTasks;

public class StorageAccountMetricsOptions
{
    public bool Enabled { get; set; }
    public int IntervalInSeconds { get; set; }
    public int CutoffThreshold { get; set; }
    public StorageAccountMetricsOptionsAccount[]? Accounts { get; set; }
}