namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusQueueMetricsOptions
{
    public bool Enabled { get; set; }
    public int IntervalInSeconds { get; set; }
    public ServiceBusQueueMetricsOptionsAccount[]? Accounts { get; set; }
}