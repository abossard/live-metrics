using System.Diagnostics;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Microsoft.Extensions.Options;
using Prometheus;
using static LiveMetrics.LiveMetricsUtils;

namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusMetricsService : IHostedService, IDisposable
{
    private readonly ServiceBusMetricsOptions _config;

    private readonly ILogger<ServiceBusMetricsService> _logger;
    private readonly Dictionary<string, ServiceBusNamespace> _serviceBusNamespaceCache = new();
    private ArmClient? _armClient;
    private bool _isWorking;

    private IEnumerable<QueueConfigurationSet>? _monitorQueues;
    private IEnumerable<TopicConfigurationSet>? _monitorTopics;
    private Timer? _timer;
    private CancellationToken _token;

    public ServiceBusMetricsService(
        ILogger<ServiceBusMetricsService> logger,
        IOptions<ServiceBusMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _logger.LogDebug("{ClassName} initialized", GetType().Name);
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
        var monitoringTasks = (_config.Accounts ?? Array.Empty<ServiceBusMetricsOptionsAccount>())
            .Select(GenerateMonitoringQueuesAsync).ToList();

        _monitorQueues = (await Task.WhenAll(monitoringTasks)).SelectMany(task => task);

        var topicsMonitoringTasks =
            await Task.WhenAll((_config.Accounts ?? Array.Empty<ServiceBusMetricsOptionsAccount>())
                .Select(GenerateMonitoringTopicsAsync).ToList());
        _monitorTopics = topicsMonitoringTasks.SelectMany(task => task);

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

    private async Task<IEnumerable<QueueConfigurationSet>> GenerateMonitoringQueuesAsync(
        ServiceBusMetricsOptionsAccount account)
    {
        _logger.LogDebug("Generating Monitoring Subject for {ResourceGroup} and {Namespace}", account.ResourceGroup,
            account.Namespace);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(account.ResourceGroup, account.Namespace, _token);
        var haystack = await serviceBusNamespace.GetServiceBusQueues().GetAllAsync().Select(item => item.Data.Name)
            .ToListAsync(_token);
        var queues = ApplyWildcardToFilterList(haystack, account.Queues);
        var result = queues.Select(queue => CreateQueueMetricsSetup(account.ResourceGroup, account.Namespace, queue))
            .ToList();
        _logger.LogDebug("Finished. Applying filters to queues: {Queues}, filtered queues: {FilteredQueues}", haystack,
            result.Select(x => x.Queue));
        return result;
    }

    private async Task<IEnumerable<TopicConfigurationSet>> GenerateMonitoringTopicsAsync(
        ServiceBusMetricsOptionsAccount account)
    {
        _logger.LogDebug("Generating Monitoring Subject for {ResourceGroup} and {Namespace}", account.ResourceGroup,
            account.Namespace);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(account.ResourceGroup, account.Namespace, _token);
        var haystack = await serviceBusNamespace.GetServiceBusTopics().GetAllAsync().Select(item => item.Data.Name)
            .ToListAsync(_token);
        var topics = ApplyWildcardToFilterList(haystack, account.Topics);
        var result = await Task.WhenAll(topics.Select(async topic =>
        {
            ServiceBusTopic topicArm = await serviceBusNamespace.GetServiceBusTopics().GetAsync(topic, _token);
            var subscriptions = await topicArm.GetServiceBusSubscriptions().GetAllAsync().Select(item => item.Data.Name)
                .ToListAsync(_token);
            return CreateTopicConfigurationSet(account.ResourceGroup, account.Namespace, topic, subscriptions);
        }));
        _logger.LogDebug("Finished. Applying filters to Topics: {Topics}, filtered topics: {FilteredTopics}", haystack,
            result.Select(x => x.Topic));
        return result;
    }

    private static DetailCountGaugeSet CreateGaugeSet(string basename, params string[] nameParams)
    {
        return new DetailCountGaugeSet(
            Metrics.CreateGauge(
                GetMetricsNameFor(basename, "active_message_count", nameParams),
                "Active message count"),
            Metrics.CreateGauge(GetMetricsNameFor(basename, "dead_letter_message_count", nameParams),
                "Dead letter message count"),
            Metrics.CreateGauge(GetMetricsNameFor(basename, "scheduled_message_count", nameParams),
                "Scheduled message count"),
            Metrics.CreateGauge(
                GetMetricsNameFor(basename, "transfer_dead_letter_message_count", nameParams),
                "Transfer dead letter message count"),
            Metrics.CreateGauge(GetMetricsNameFor(basename, "transfer_message_count", nameParams),
                "Transfer message count")
        );
    }

    private static Gauge CreateMetricMessageCount(string basename, params string[] nameParams)
    {
        return Metrics.CreateGauge(GetMetricsNameFor(basename, "message_count", nameParams), "Message count");
    }

    private static QueueConfigurationSet CreateQueueMetricsSetup(string? resourceGroup, string? namespaceName,
        string queue)
    {
        Debug.Assert(resourceGroup != null, nameof(resourceGroup) + " != null");
        Debug.Assert(namespaceName != null, nameof(namespaceName) + " != null");
        return new QueueConfigurationSet(
            resourceGroup,
            namespaceName,
            queue, 
            CreateMetricMessageCount("queue", resourceGroup, namespaceName),
            CreateGaugeSet("queue", resourceGroup, namespaceName, queue));
    }

    private static TopicConfigurationSet CreateTopicConfigurationSet(string? resourceGroup, string? namespaceName,
        string topic, IEnumerable<string> subscriptions)
    {
        Debug.Assert(resourceGroup != null, nameof(resourceGroup) + " != null");
        Debug.Assert(namespaceName != null, nameof(namespaceName) + " != null");
        return new TopicConfigurationSet(
            resourceGroup,
            namespaceName,
            topic,
            CreateGaugeSet("topic", resourceGroup, namespaceName, topic),
            Array.Empty<SubscriptionConfigurationSet>());
    }

    private async Task<ServiceBusNamespace> GetServiceBusNamespaceAsync(string? resourceGroup, string? namespaceName,
        CancellationToken cancellationToken = default)
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

    private static void UpdateGaugeSet(DetailCountGaugeSet detailCountGauges, 
        long? activeMessageCount, 
        long? deadLetterMessageCount, 
        long? scheduledMessageCount,
        long? transferDeadLetterMessageCount,
        long? transferMessageCount
    )
    {
        if (activeMessageCount != null)
            detailCountGauges.ActiveMessageCount.Set((double)activeMessageCount);
        if (deadLetterMessageCount != null)
            detailCountGauges.DeadLetterMessageCount.Set((double)deadLetterMessageCount);
        if (scheduledMessageCount != null)
            detailCountGauges.ScheduledMessageCount.Set(
                (double)scheduledMessageCount);
        if (transferDeadLetterMessageCount != null)
            detailCountGauges.TransferDeadLetterMessageCount.Set((double)transferDeadLetterMessageCount);
        if (transferMessageCount != null)
            detailCountGauges.TransferMessageCount.Set((double)transferMessageCount);
    } 
    private async Task CreateUpdateMetricForServiceBusTopicTask(TopicConfigurationSet topicConfigurationSet,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting to update metrics for {NamespaceName} and topic: {Topic}",
            topicConfigurationSet.NamespaceName, topicConfigurationSet.Topic);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(topicConfigurationSet.ResourceGroup,
            topicConfigurationSet.NamespaceName, cancellationToken);
        ServiceBusTopic topic = await serviceBusNamespace.GetServiceBusTopics().GetAsync(topicConfigurationSet.Topic);
        UpdateGaugeSet(topicConfigurationSet.TopicDetailCountGauges,
            topic.Data.CountDetails.ActiveMessageCount,
            topic.Data.CountDetails.DeadLetterMessageCount,
            topic.Data.CountDetails.ScheduledMessageCount,
            topic.Data.CountDetails.TransferDeadLetterMessageCount,
            topic.Data.CountDetails.TransferMessageCount);
    }
    private async Task CreateUpdateMetricForServiceBusQueueTask(QueueConfigurationSet queueConfigurationSet,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting to update metrics for {NamespaceName} and {Queue}",
            queueConfigurationSet.NamespaceName,
            queueConfigurationSet.Queue);
        var serviceBusNamespace = await GetServiceBusNamespaceAsync(queueConfigurationSet.ResourceGroup,
            queueConfigurationSet.NamespaceName, cancellationToken);

        ServiceBusQueue queue = await serviceBusNamespace.GetServiceBusQueues()
            .GetAsync(queueConfigurationSet.Queue, cancellationToken);
        UpdateGaugeSet(queueConfigurationSet.DetailCountGauges, queue.Data.CountDetails.ActiveMessageCount,
            queue.Data.CountDetails.DeadLetterMessageCount, queue.Data.CountDetails.ScheduledMessageCount,
            queue.Data.CountDetails.TransferDeadLetterMessageCount, queue.Data.CountDetails.TransferMessageCount);
        if (queue.Data.MessageCount != null)
        {
            queueConfigurationSet.MessageCount.Set((double)queue.Data.MessageCount);
        }
        _logger.LogDebug(
            "Finished updating metrics for {NamespaceName} and {Queue}: {@CountDetails}, {MessageCount}",
            queueConfigurationSet.NamespaceName, queueConfigurationSet.Queue,
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
        var queueTasks = (_monitorQueues ?? Array.Empty<QueueConfigurationSet>())
            .Select(item => CreateUpdateMetricForServiceBusQueueTask(item, _token))
            .ToArray();
        var topicTasks = (_monitorTopics ?? Array.Empty<TopicConfigurationSet>())
            .Select(item => CreateUpdateMetricForServiceBusTopicTask(item, _token))
            .ToArray();
        Task.WaitAll(queueTasks.Concat(topicTasks).ToArray(), _token);
        _logger.LogDebug("{ClassName} finished", GetType().Name);
        _isWorking = false;
    }

    private record struct DetailCountGaugeSet(Gauge ActiveMessageCount, Gauge DeadLetterMessageCount, Gauge ScheduledMessageCount,
        Gauge TransferDeadLetterMessageCount, Gauge TransferMessageCount);

    private record struct QueueConfigurationSet(string? ResourceGroup, string? NamespaceName,
        string Queue, Gauge MessageCount, DetailCountGaugeSet DetailCountGauges);

    private record struct TopicConfigurationSet(string? ResourceGroup, string? NamespaceName,
        string Topic, DetailCountGaugeSet TopicDetailCountGauges, IEnumerable<SubscriptionConfigurationSet> SubscriptionConfigurationSets);

    private record struct SubscriptionConfigurationSet(string? ResourceGroup, string? NamespaceName,
        string Topic, string Subscription, DetailCountGaugeSet SubscriptionDetailCountGauges);
}