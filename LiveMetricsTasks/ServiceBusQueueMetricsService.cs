using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.Storage.Blobs;
using Microsoft.Azure.Management.ServiceBus;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LiveMetrics.LiveMetricsTasks;

public class ServiceBusQueueMetricsService : IHostedService, IDisposable
{
    private readonly ServiceBusQueueMetricsOptions _config;
    
    private readonly ILogger<ServiceBusQueueMetricsService> _logger;

    private readonly IEnumerable<ConfigurationSet> _monitorSubjects;
    private Timer? _timer;
    private CancellationToken _token;
    private ArmClient _armClient;

    public ServiceBusQueueMetricsService(
        ILogger<ServiceBusQueueMetricsService> logger,
        IOptions<ServiceBusQueueMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _monitorSubjects =
            (_config.Accounts ?? Array.Empty<string>()).Select(GetMetricsSetupFromConnectionString);
        _logger.LogDebug("{ClassName} initialized", GetType().Name);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(_config.IntervalInSeconds));
        _token = cancellationToken;
        _armClient = new ArmClient(new DefaultAzureCredential());

        _logger.LogDebug("{ClassName} started", GetType().Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.LogDebug("{ClassName} stopped", GetType().Name);
        return Task.CompletedTask;
    }

    private static ConfigurationSet GetMetricsSetupFromConnectionString(ServiceBusQueueMetricsOptionsAccount account)
    {
        /*account.Queues*/;
        var connectionStringDictionary = connectionString.Split(',').ToDictionary(
            entry => entry.Split('=')[0],
            entry => entry.Split('=')[1]);
        var endpoint = connectionStringDictionary["Endpoint"];
        var entityPath = connectionStringDictionary["EntityPath"];
        var namespaceName = Regex.Match(endpoint, @"sb://([^/]*)").Groups[1].Value;
        return new ConfigurationSet(connectionString,
            connectionStringDictionary["Endpoint"],
            namespaceName,
            connectionStringDictionary["EntityPath"],
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_active_message_count", "Active message count"),
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_dead_letter_message_count", "Dead letter message count"),
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_scheduled_message_count", "Scheduled message count"),
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_transfer_dead_letter_message_count",
                "Transfer dead letter message count"),
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_transfer_message_count", "Transfer message count"),
            Metrics.CreateGauge($"{namespaceName}_{entityPath}_message_count", "Message count")
        );
    }
    

    private async Task UpdateMetricForServiceBusQueue(ConfigurationSet configurationSet,
        CancellationToken cancellationToken = default)
    {
        
        
        
    }

    private void DoWork(object? state)
    {
        _logger.LogDebug("StorageAccountMetricsService is working");
        var accountTasks = _monitorSubjects
            .Select(item => UpdateMetricForServiceBusQueue(
                item.account.ConnectionString, item.containers, _token))
            .ToArray();
        Task.WaitAll(accountTasks);
        _logger.LogDebug("StorageAccountMetricsService finished");
    }

    private record struct ConfigurationSet(string ConnectionString, string Endpoint, string NamespaceName,
        string EntityPath, Gauge ActiveMessageCount, Gauge DeadLetterMessageCount, Gauge ScheduledMessageCount,
        Gauge TransferDeadLetterMessageCount, Gauge TransferMessageCount, Gauge MessageCount);
}