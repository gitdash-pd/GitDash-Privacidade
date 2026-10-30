using GitDashBackend.Configurations;
using Microsoft.Extensions.Options;

namespace GitDashBackend.Services.Implementations;

public class RetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RetentionSettings _settings;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IServiceScopeFactory scopeFactory,
        IOptions<RetentionSettings> settings, ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));

        try
        {
            if (_settings.RunOnStartup)
                await PurgeAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await PurgeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
            var result = await service.PurgeExpiredAsync(DateTime.UtcNow, cancellationToken);

            _logger.LogInformation(
                "Retention cleanup: {HistoryCount} history entries and {LogCount} access logs deleted.",
                result.History, result.Logs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention cleanup failed. It will be retried at the next daily run.");
        }
    }
}
