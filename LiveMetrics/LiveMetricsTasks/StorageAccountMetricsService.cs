using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Prometheus;
using static LiveMetrics.LiveMetricsUtils;

namespace LiveMetrics.LiveMetricsTasks;

public class StorageAccountMetricsService : IHostedService, IDisposable
{
    private const string AccountNamePattern = "AccountName=([^;]*)";
    private readonly StorageAccountMetricsOptions _config;

    //private bool _isWorking;
    private readonly Dictionary<string, bool> _isWorkingCache = new();
    private readonly ILogger<StorageAccountMetricsService> _logger;
    private readonly IEnumerable<ConfigurationRecord> _monitorSubjects;

    private Timer? _timer;
    private CancellationToken _token;

    public StorageAccountMetricsService(
        ILogger<StorageAccountMetricsService> logger,
        IOptions<StorageAccountMetricsOptions> config
    )
    {
        _logger = logger;
        _config = config.Value;
        _monitorSubjects = (_config.Accounts ?? Array.Empty<StorageAccountMetricsOptionsAccount>()).Select(
            account => new ConfigurationRecord(
                account,
                (account.Containers ?? Array.Empty<string>()).Select(container =>
                    new ContainerMetricsRecord(
                        container,
                        CreateCounterMetricFor(
                            account.ConnectionString ??
                            throw new InvalidOperationException(
                                "ConnectionString is null in account, check appsettings.json"), container),
                        CreateThresholdMetricFor(
                            account.ConnectionString ??
                            throw new InvalidOperationException(
                                "ConnectionString is null in account, check appsettings.json"), container)
                    ))));

        _logger.LogDebug("StorageAccountMetricsService initialized");
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
            _logger.LogDebug("{ClassName} is disabled", GetType().Name);
            return Task.CompletedTask;
        }

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
        var metricName = GetMetricsNameFor("blob", "count", accountName, container);
        return Metrics.CreateGauge(metricName, "Items in Container");
    }

    private static Gauge CreateThresholdMetricFor(string connectionString, string container)
    {
        var accountName = Regex.Match(connectionString, AccountNamePattern).Groups[1].Value;
        var metricName = GetMetricsNameFor("blob", "threshold", accountName, container);
        return Metrics.CreateGauge(metricName, "1 for above  threshold, 0 for below threshold");
    }

    private async Task MeasureBlobCount(BlobContainerClient container, Gauge countMetric, Gauge threshold,
        string filePrefix = "", CancellationToken cancellationToken = default)
    {
        var lockKey = container.Uri.ToString();
        if (_isWorkingCache.TryGetValue(lockKey, out var isWorking))
            if (isWorking)
            {
                _logger.LogDebug("Already working on {ContainerName}", container.Name);
                return;
            }

        _isWorkingCache[lockKey] = true;
        var count = 0;
        var respectThreshold = _config.CutoffThreshold > 0;
        _logger.LogDebug("Start to measuring blob count for {ContainerName}", container.Name);
        await foreach (var unused in container.GetBlobsAsync(prefix: filePrefix, cancellationToken: cancellationToken))
        {
            count += 1;
            if (!respectThreshold || count < _config.CutoffThreshold) continue;
            _logger.LogDebug("Cutoff threshold reached for {MetricName} and cutoff {Cutoff}", threshold.Name,
                _config.CutoffThreshold);
            break;
        }

        countMetric.Set(count);
        threshold.Set(count >= _config.CutoffThreshold ? 1 : 0);
        _logger.LogDebug("Set {MetricName} to {Count}", countMetric.Name, count);
        _logger.LogDebug("Finished measuring blob count for {ContainerName}", container.Name);
        _isWorkingCache[lockKey] = false;
    }

    private async Task UpdateMetricForBlobAccount(string? connectionString,
        IEnumerable<ContainerMetricsRecord> containers,
        CancellationToken cancellationToken = default)
    {
        var blobServiceClient = new BlobServiceClient(connectionString);
        var containerTasks = containers.Select(async container => await
            MeasureBlobCount(
                blobServiceClient.GetBlobContainerClient(container.Container),
                container.Count,
                container.Threshold,
                cancellationToken: cancellationToken
            )
        );
        await Task.WhenAll(containerTasks);
    }

    private void DoWork(object? state)
    {
        _logger.LogDebug("StorageAccountMetricsService is working");
        var accountTasks = _monitorSubjects
            .Select(item => UpdateMetricForBlobAccount(item.Account.ConnectionString, item.Containers, _token))
            .ToArray();
        Task.WaitAll(accountTasks);
        _logger.LogDebug("StorageAccountMetricsService finished");
    }

    private record struct ConfigurationRecord(StorageAccountMetricsOptionsAccount Account,
        IEnumerable<ContainerMetricsRecord> Containers);

    private record struct ContainerMetricsRecord(string Container, Gauge Count, Gauge Threshold);
}