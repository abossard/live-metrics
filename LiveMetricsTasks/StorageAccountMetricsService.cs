using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LiveMetrics.LiveMetricsTasks;

public class StorageAccountMetricsService : IHostedService, IDisposable {
    private readonly ILogger<StorageAccountMetricsService> _logger;
    private readonly StorageAccountMetricsOptions _config;
    private Timer? _timer;
    private bool _isWorking;
    private CancellationToken _token;
    private const string AccountNamePattern = "AccountName=([^;]*)";
    private readonly IEnumerable<(StorageAccountMetricsServiceOptionEntry account, IEnumerable<(string container, IGauge metric)> containers)> _monitorSubjects;

    public StorageAccountMetricsService(
        ILogger<StorageAccountMetricsService> logger, 
        IOptions<StorageAccountMetricsOptions> config
    ) {
        _logger = logger;
        _config = config.Value;
        _monitorSubjects = (_config.Accounts ?? Array.Empty<StorageAccountMetricsServiceOptionEntry>()).Select(
            account =>
                (account,
                    containers: account.Containers.Select(container =>
                        (container, metric: CreateCounterFor(account.ConnectionString, container)))));
    }

    private static IGauge CreateCounterFor(string connectionString, string container)
    {
        var accountName = Regex.Match(connectionString, AccountNamePattern).Groups[1].Value;
        return Metrics.CreateGauge($"{accountName}_{container}_gauge", "Items in Container");
    }
    
    private static async Task<int> MeasureBlobCount(BlobContainerClient container, IGauge metric, string filePrefix = "",  CancellationToken cancellationToken = default)
    {
        var count = 0;
        await foreach (var blob in container.GetBlobsAsync(prefix: filePrefix, cancellationToken: cancellationToken))
        {
            count += 1;
        }
        metric.Set(count);
        return count;
    }

    private static async Task UpdateMetricForBlobAccount(string connectionString, IEnumerable<(string container, IGauge metric)> containers, CancellationToken cancellationToken = default)
    {
        var blobServiceClient = new BlobServiceClient(connectionString);
        var containerTasks = containers.Select(async container => await 
            MeasureBlobCount(blobServiceClient.GetBlobContainerClient(container.container), container.metric, cancellationToken: cancellationToken)
        );
        await Task.WhenAll(containerTasks);
    }
    
    public Task StartAsync(CancellationToken cancellationToken) {
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(_config.IntervalInSeconds));
        _token = cancellationToken;
        return Task.CompletedTask;
    }

    private void DoWork(object? state) {
        if (_isWorking) {
            return;
        }
        _isWorking = true;
        var accountTasks = _monitorSubjects.Select(item => UpdateMetricForBlobAccount(item.account.ConnectionString, item.containers, _token)).ToArray();
        Task.WaitAll(accountTasks);
        _isWorking = false;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() {
        _timer?.Dispose();
    }
}