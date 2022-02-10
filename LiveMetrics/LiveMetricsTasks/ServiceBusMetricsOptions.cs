namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusMetricsOptions
{
    public bool Enabled { get; set; }
    public int IntervalInSeconds { get; set; }

    public ServiceBusMetricsOptionsAccount[]? Accounts { get; set; }
}