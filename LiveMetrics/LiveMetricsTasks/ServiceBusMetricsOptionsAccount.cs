// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace LiveMetrics.LiveMetricsTasks;

// ReSharper disable once ClassNeverInstantiated.Global
public class ServiceBusMetricsOptionsAccount
{
    public string? ResourceGroup { get; set; }
    public string? Namespace { get; set; }
    public string[]? Queues { get; set; }

    public string[]? Topics { get; set; }
}