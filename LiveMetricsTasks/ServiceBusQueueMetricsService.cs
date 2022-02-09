using System.Diagnostics;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusQueueMetricsService : IHostedService, IDisposable
{
    private readonly ServiceBusQueueMetricsOptions _config;

    private readonly ILogger<ServiceBusQueueMetricsService> _logger;

    private readonly IEnumerable<ConfigurationSet> _monitorSubjects;
    private readonly Dictionary<string, ServiceBusNamespace> _serviceBusNamespaceCache = new();
    private ArmClient? _armClient;
    private Timer? _timer;
    private CancellationToken _token;
    private bool _isWorking = false;

    public ServiceBusQueueMetricsService(
        ILogger<ServiceBusQueueMetricsService> logger,
        IOptions<ServiceBusQueueMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _monitorSubjects = (_config.Accounts ?? Array.Empty<ServiceBusQueueMetricsOptionsAccount>())
            .Select(GetMetricsSetupFromConnectionString).SelectMany(x => x);
        _logger.LogDebug("{ClassName} initialized", GetType().Name);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("{ClassName} disabled", GetType().Name);
            return Task.CompletedTask;
        }
        _logger.LogDebug("Acquiring Azure Resource Manager credentials");
        _armClient = new ArmClient(new DefaultAzureCredential());
        _logger.LogDebug("Finished acquiring Azure Resource Manager credentials");
        
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(_config.IntervalInSeconds));
        _token = cancellationToken;
        _logger.LogDebug("{ClassName} started", GetType().Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.LogDebug("{ClassName} stopped", GetType().Name);
        return Task.CompletedTask;
    }

    private static IEnumerable<ConfigurationSet> GetMetricsSetupFromConnectionString(
        ServiceBusQueueMetricsOptionsAccount account)
    {
        return account.Queues.Select(queue => new ConfigurationSet(
            account.ResourceGroup,
            account.Namespace,
            queue,
            Metrics.CreateGauge($"{account.Namespace}_{queue}_active_message_count", "Active message count"),
            Metrics.CreateGauge($"{account.Namespace}_{queue}_dead_letter_message_count", "Dead letter message count"),
            Metrics.CreateGauge($"{account.Namespace}_{queue}_scheduled_message_count", "Scheduled message count"),
            Metrics.CreateGauge($"{account.Namespace}_{queue}_transfer_dead_letter_message_count",
                "Transfer dead letter message count"),
            Metrics.CreateGauge($"{account.Namespace}_{queue}_transfer_message_count", "Transfer message count"),
            Metrics.CreateGauge($"{account.Namespace}_{queue}_message_count", "Message count")
        ));
    }


    private async Task CreateUpdateMetricForServiceBusQueueTask(ConfigurationSet configurationSet,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{configurationSet.ResourceGroup}_{configurationSet.NamespaceName}";
        _logger.LogDebug("Starting to update metrics for {NamespaceName} and {Queue}", configurationSet.NamespaceName,
            configurationSet.Queue);
        if (!_serviceBusNamespaceCache.TryGetValue(cacheKey, out var serviceBusNamespace))
        {
            Debug.Assert(_armClient != null, nameof(_armClient) + " != null");
            var sub = await _armClient.GetDefaultSubscriptionAsync(cancellationToken);
            ResourceGroup resourceGroup =
                await sub.GetResourceGroups().GetAsync(configurationSet.ResourceGroup, cancellationToken);
            serviceBusNamespace = await resourceGroup.GetServiceBusNamespaces()
                .GetAsync(configurationSet.NamespaceName, cancellationToken);
            _serviceBusNamespaceCache[cacheKey] = serviceBusNamespace;
        }

        ServiceBusQueue queue = await serviceBusNamespace.GetServiceBusQueues()
            .GetAsync(configurationSet.Queue, cancellationToken);

        if (queue.Data.CountDetails.ActiveMessageCount != null)
            configurationSet.ActiveMessageCount.Set((double)queue.Data.CountDetails.ActiveMessageCount);
        if (queue.Data.CountDetails.DeadLetterMessageCount != null)
            configurationSet.DeadLetterMessageCount.Set((double)queue.Data.CountDetails.DeadLetterMessageCount);
        if (queue.Data.CountDetails.ScheduledMessageCount != null)
            configurationSet.ScheduledMessageCount.Set((double)queue.Data.CountDetails.ScheduledMessageCount);
        if (queue.Data.CountDetails.TransferDeadLetterMessageCount != null)
            configurationSet.TransferDeadLetterMessageCount.Set((double)queue.Data.CountDetails
                .TransferDeadLetterMessageCount);
        if (queue.Data.CountDetails.TransferMessageCount != null)
            configurationSet.TransferMessageCount.Set((double)queue.Data.CountDetails.TransferMessageCount);
        if (queue.Data.MessageCount != null) configurationSet.MessageCount.Set((double)queue.Data.MessageCount);
        _logger.LogDebug("Finished updating metrics for {NamespaceName} and {Queue}: {@CountDetails}, {MessageCount}",
            configurationSet.NamespaceName, configurationSet.Queue,
            queue.Data.CountDetails, queue.Data.MessageCount);
    }

    private void DoWork(object? state)
    {
        if (_isWorking)
        {
            _logger.LogDebug("{ClassName} is already working", GetType().Name);
            return;
        }
        _isWorking = true;
        _logger.LogDebug("{ClassName} is working", GetType().Name);
        var accountTasks = _monitorSubjects
            .Select(item => CreateUpdateMetricForServiceBusQueueTask(item, _token))
            .ToArray();
        Task.WaitAll(accountTasks, _token);
        _logger.LogDebug("{ClassName} finished", GetType().Name);
        _isWorking = false;
    }

    private record struct ConfigurationSet(string ResourceGroup, string NamespaceName,
        string Queue, Gauge ActiveMessageCount, Gauge DeadLetterMessageCount, Gauge ScheduledMessageCount,
        Gauge TransferDeadLetterMessageCount, Gauge TransferMessageCount, Gauge MessageCount);
}