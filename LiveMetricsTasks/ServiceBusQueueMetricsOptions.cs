namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusQueueMetricsOptions
{
    
    public int IntervalInSeconds { get; set; }
    public ServiceBusQueueMetricsOptionsAccount[] Accounts { get; set; }
}