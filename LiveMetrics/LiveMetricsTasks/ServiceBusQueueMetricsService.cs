using System.Diagnostics;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Microsoft.Extensions.Options;
using Prometheus;
using static LiveMetrics.LiveMetricsUtils;

namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusQueueMetricsService : IHostedService, IDisposable
{
    private readonly ServiceBusQueueMetricsOptions _config;

    private readonly ILogger<ServiceBusQueueMetricsService> _logger;

    private IEnumerable<ConfigurationSet>? _monitorSubjects;
    private readonly Dictionary<string, ServiceBusNamespace> _serviceBusNamespaceCache = new();
    private ArmClient? _armClient;
    private bool _isWorking;
    private Timer? _timer;
    private CancellationToken _token;

    public ServiceBusQueueMetricsService(
        ILogger<ServiceBusQueueMetricsService> logger,
        IOptions<ServiceBusQueueMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _logger.LogDebug("{ClassName} initialized", GetType().Name);
    }

    private async Task<IEnumerable<ConfigurationSet>> GenerateMonitoringSubjectForAsync(ServiceBusQueueMetricsOptionsAccount account)
    {
        _logger.LogDebug("Generating Monitoring Subject for {ResourceGroup} and {Namespace}", account.ResourceGroup, account.Namespace);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(account.ResourceGroup, account.Namespace, _token);
        var haystack = await serviceBusNamespace.GetServiceBusQueues().GetAllAsync().Select(item=> item.Data.Name).ToListAsync(_token);
        var queues = ApplyWildcardToFilterList(haystack, account.Queues);
        var result = queues.Select(queue => GetMetricsSetupFromConnectionString(account.ResourceGroup, account.Namespace, queue)).ToList();
        _logger.LogDebug("Finished. Applying filters to queues: {Queues}, filtered queues: {FilteredQueues}", haystack, result.Select(x => x.Queue));
        return result;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("{ClassName} disabled", GetType().Name);
            return;
        }

        _logger.LogDebug("Acquiring Azure Resource Manager credentials");
        _armClient = new ArmClient(new DefaultAzureCredential());
        _logger.LogDebug("Finished acquiring Azure Resource Manager credentials");
        var monitoringTasks = (_config.Accounts ?? Array.Empty<ServiceBusQueueMetricsOptionsAccount>()).Select(GenerateMonitoringSubjectForAsync).ToList();
        await Task.WhenAll(monitoringTasks);
        _monitorSubjects = monitoringTasks.SelectMany(task => task.Result);
        
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(_config.IntervalInSeconds));
        _token = cancellationToken;
        _logger.LogDebug("{ClassName} started", GetType().Name);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.LogDebug("{ClassName} stopped", GetType().Name);
        return Task.CompletedTask;
    }

    private static ConfigurationSet GetMetricsSetupFromConnectionString(string? resourceGroup, string? namespaceName, string queue)
    {
        return new ConfigurationSet(
            resourceGroup,
            namespaceName,
            queue,
            Metrics.CreateGauge(
                GetMetricsNameFor("queue", "active_message_count", namespaceName,
                    queue),
                "Active message count"),
            Metrics.CreateGauge(GetMetricsNameFor("queue", "dead_letter_message_count", namespaceName, queue),
                "Dead letter message count"),
            Metrics.CreateGauge(GetMetricsNameFor("queue", "scheduled_message_count", namespaceName, queue),
                "Scheduled message count"),
            Metrics.CreateGauge(
                GetMetricsNameFor("queue", "transfer_dead_letter_message_count", namespaceName, queue),
                "Transfer dead letter message count"),
            Metrics.CreateGauge(GetMetricsNameFor("queue", "transfer_message_count", namespaceName, queue),
                "Transfer message count"),
            Metrics.CreateGauge(GetMetricsNameFor("queue", "message_count", namespaceName, queue), "Message count")
        );
    }

    private async Task<ServiceBusNamespace> GetServiceBusNamespaceAsync(string? resourceGroup, string? namespaceName, CancellationToken cancellationToken=default)
    {
        var cacheKey = $"{resourceGroup}_{namespaceName}";
        if (_serviceBusNamespaceCache.TryGetValue(cacheKey, out var serviceBusNamespace)) return serviceBusNamespace;
        Debug.Assert(_armClient != null, nameof(_armClient) + " != null");
        var sub = await _armClient.GetDefaultSubscriptionAsync(cancellationToken);
        ResourceGroup resourceGroupArm =
            await sub.GetResourceGroups().GetAsync(resourceGroup, cancellationToken);
        serviceBusNamespace = await resourceGroupArm.GetServiceBusNamespaces()
            .GetAsync(namespaceName, cancellationToken);
        _serviceBusNamespaceCache[cacheKey] = serviceBusNamespace;
        return serviceBusNamespace;
    }
    private async Task CreateUpdateMetricForServiceBusQueueTask(ConfigurationSet configurationSet,
        CancellationToken cancellationToken = default)
    {
        
        _logger.LogDebug("Starting to update metrics for {NamespaceName} and {Queue}", configurationSet.NamespaceName,
            configurationSet.Queue);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(configurationSet.ResourceGroup,
            configurationSet.NamespaceName, cancellationToken);

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
        var accountTasks = (_monitorSubjects ?? Array.Empty<ConfigurationSet>())
            .Select(item => CreateUpdateMetricForServiceBusQueueTask(item, _token))
            .ToArray();
        Task.WaitAll(accountTasks, _token);
        _logger.LogDebug("{ClassName} finished", GetType().Name);
        _isWorking = false;
    }

    private record struct ConfigurationSet(string? ResourceGroup, string? NamespaceName,
        string Queue, Gauge ActiveMessageCount, Gauge DeadLetterMessageCount, Gauge ScheduledMessageCount,
        Gauge TransferDeadLetterMessageCount, Gauge TransferMessageCount, Gauge MessageCount);
}