using GitDashBackend.Configurations;
using GitDashBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitDashBackend.Services.Implementations;

public class RetentionService
{
    private readonly AppDbContext _dbContext;
    private readonly RetentionSettings _settings;

    public RetentionService(AppDbContext dbContext, IOptions<RetentionSettings> settings)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    public async Task<(int History, int Logs)> PurgeExpiredAsync(
        DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Unspecified);
        var historyLimit = now.AddDays(-_settings.HistoryDays);
        var logLimit = now.AddDays(-_settings.LogDays);

        var history = await _dbContext.Repositories
            .Where(r => r.LastVisited < historyLimit)
            .ExecuteDeleteAsync(cancellationToken);

        var logs = await _dbContext.Logs
            .Where(l => l.Created < logLimit)
            .ExecuteDeleteAsync(cancellationToken);

        return (history, logs);
    }
}
