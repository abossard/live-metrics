using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LiveMetrics.LiveMetricsTasks;

public class StorageAccountMetricsService : IHostedService, IDisposable
{
    private const string AccountNamePattern = "AccountName=([^;]*)";
    private readonly StorageAccountMetricsOptions _config;
    private readonly ILogger<StorageAccountMetricsService> _logger;

    private readonly IEnumerable<(
            StorageAccountMetricsServiceOptionEntry account,
            IEnumerable<(string container,
                Gauge count,
                Gauge threshold)> containers)>
        _monitorSubjects;

    private bool _isWorking;
    private Timer? _timer;
    private CancellationToken _token;

    public StorageAccountMetricsService(
        ILogger<StorageAccountMetricsService> logger,
        IOptions<StorageAccountMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _monitorSubjects = (_config.Accounts ?? Array.Empty<StorageAccountMetricsServiceOptionEntry>()).Select(
            account =>
                (account,
                    containers: (account.Containers ?? Array.Empty<string>()).Select(container =>
                        (container,
                            count: CreateCounterMetricFor(
                                account.ConnectionString ??
                                throw new InvalidOperationException(
                                    "ConnectionString is null in account, check appsettings.json"), container),
                            threshold: CreateThresholdMetricFor(
                                account.ConnectionString ??
                                throw new InvalidOperationException(
                                    "ConnectionString is null in account, check appsettings.json"), container)))));
        _logger.LogDebug("StorageAccountMetricsService initialized");
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
        _logger.LogDebug("StorageAccountMetricsService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _logger.LogDebug("StorageAccountMetricsService stopped");
        return Task.CompletedTask;
    }

    private static Gauge CreateCounterMetricFor(string connectionString, string container)
    {
        var accountName = Regex.Match(connectionString, AccountNamePattern).Groups[1].Value;
        return Metrics.CreateGauge($"blob_{accountName}_{container}_count".Replace("-", "_").ToLowerInvariant(),
            "Items in Container");
    }

    private static Gauge CreateThresholdMetricFor(string connectionString, string container)
    {
        var accountName = Regex.Match(connectionString, AccountNamePattern).Groups[1].Value;
        return Metrics.CreateGauge($"blob_{accountName}_{container}_threshold".Replace("-", "_").ToLowerInvariant(),
            "1 for above  threshold, 0 for below threshold");
    }

    private async Task<int> MeasureBlobCount(BlobContainerClient container, Gauge countMetric, Gauge threshold,
        string filePrefix = "", CancellationToken cancellationToken = default)
    {
        var count = 0;
        var respectThreshold = _config.CutoffThreshold > 0;
        await foreach (var unused in container.GetBlobsAsync(prefix: filePrefix, cancellationToken: cancellationToken))
        {
            count += 1;
            if (respectThreshold && count >= _config.CutoffThreshold)
            {
                _logger.LogDebug("Cutoff threshold reached for {MetricName} and cutoff {Cutoff}", threshold.Name, _config.CutoffThreshold);
                break;
            };
        }

        countMetric.Set(count);
        threshold.Set(count >= _config.CutoffThreshold ? 1 : 0);
        _logger.LogDebug("Set {MetricName} to {Count}", countMetric.Name, count);
        return count;
    }

    private async Task UpdateMetricForBlobAccount(string? connectionString,
        IEnumerable<(string container, Gauge count, Gauge threshold)> containers,
        CancellationToken cancellationToken = default)
    {
        var blobServiceClient = new BlobServiceClient(connectionString);
        var containerTasks = containers.Select(async container => await
            MeasureBlobCount(
                blobServiceClient.GetBlobContainerClient(container.container),
                container.count,
                container.threshold,
                cancellationToken: cancellationToken
            )
        );
        await Task.WhenAll(containerTasks);
    }

    private void DoWork(object? state)
    {
        if (_isWorking)
        {
            _logger.LogDebug("StorageAccountMetricsService is already working");
            return;
        }

        _logger.LogDebug("StorageAccountMetricsService is working");
        _isWorking = true;
        var accountTasks = _monitorSubjects
            .Select(item => UpdateMetricForBlobAccount(item.account.ConnectionString, item.containers, _token))
            .ToArray();
        Task.WaitAll(accountTasks);
        _isWorking = false;
        _logger.LogDebug("StorageAccountMetricsService finished");
    }
}