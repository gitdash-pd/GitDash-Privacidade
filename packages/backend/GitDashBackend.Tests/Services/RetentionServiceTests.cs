using GitDashBackend.Configurations;
using GitDashBackend.Data;
using GitDashBackend.Models;
using GitDashBackend.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitDashBackend.Tests.Services;

public class RetentionServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    public RetentionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new TestDbContext(options);
        _context.Database.EnsureCreated();
        _context.Users.Add(new User { Id = 1, Username = "conta-teste", Created = Stored(Now) });
        _context.SaveChanges();
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task History_OnlyDeletesEntriesPastTheLimit(int hoursPastLimit, int deleted)
    {
        AddRepository("teste/historico", Now.AddDays(-90).AddHours(-hoursPastLimit));
        await _context.SaveChangesAsync();

        var result = await CreateService().PurgeExpiredAsync(Now);

        Assert.Equal(deleted, result.History);
        Assert.Equal(0, result.Logs);
        Assert.Equal(1 - deleted, await _context.Repositories.CountAsync());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task Logs_OnlyDeletesEntriesPastTheLimit(int hoursPastLimit, int deleted)
    {
        AddLog(Now.AddDays(-180).AddHours(-hoursPastLimit));
        await _context.SaveChangesAsync();

        var result = await CreateService().PurgeExpiredAsync(Now);

        Assert.Equal(0, result.History);
        Assert.Equal(deleted, result.Logs);
        Assert.Equal(1 - deleted, await _context.Logs.CountAsync());
    }

    [Fact]
    public async Task History_KeepsOldRepositoryWhenItWasRevisited()
    {
        AddRepository("teste/revisitado", Now.AddDays(-1));
        await _context.SaveChangesAsync();

        var result = await CreateService().PurgeExpiredAsync(Now);

        Assert.Equal(0, result.History);
        Assert.Equal("teste/revisitado", (await _context.Repositories.SingleAsync()).Name);
    }

    [Fact]
    public async Task Purge_UsesConfiguredDaysForEachTable()
    {
        AddRepository("teste/recente", Now.AddDays(-6));
        AddRepository("teste/expirado", Now.AddDays(-8));
        AddLog(Now.AddDays(-13));
        AddLog(Now.AddDays(-15));
        await _context.SaveChangesAsync();
        var service = CreateService(new RetentionSettings { HistoryDays = 7, LogDays = 14 });

        var result = await service.PurgeExpiredAsync(Now);

        Assert.Equal((1, 1), result);
        Assert.Equal("teste/recente", (await _context.Repositories.SingleAsync()).Name);
        Assert.Equal(Stored(Now.AddDays(-13)), (await _context.Logs.SingleAsync()).Created);
    }

    [Fact]
    public async Task Purge_ReturnsCountsAndDoesNotDeleteUsersOrRecentData()
    {
        AddRepository("teste/recente", Now.AddDays(-30));
        AddRepository("teste/expirado-1", Now.AddDays(-120));
        AddRepository("teste/expirado-2", Now.AddDays(-150));
        AddLog(Now.AddDays(-100));
        AddLog(Now.AddDays(-200));
        await _context.SaveChangesAsync();
        var service = CreateService();

        var first = await service.PurgeExpiredAsync(Now);
        var second = await service.PurgeExpiredAsync(Now);

        Assert.Equal((2, 1), first);
        Assert.Equal((0, 0), second);
        Assert.Equal("teste/recente", (await _context.Repositories.SingleAsync()).Name);
        Assert.Equal(Stored(Now.AddDays(-100)), (await _context.Logs.SingleAsync()).Created);
        Assert.Equal(1, await _context.Users.CountAsync());
    }

    private RetentionService CreateService(RetentionSettings? settings = null) =>
        new(_context, Options.Create(settings ?? new RetentionSettings()));

    private void AddRepository(string name, DateTime lastVisited)
    {
        _context.Repositories.Add(new Repository
        {
            UserId = 1,
            Name = name,
            Created = Stored(Now.AddDays(-365)),
            LastVisited = Stored(lastVisited),
            VisitsNumber = 1
        });
    }

    private void AddLog(DateTime created)
    {
        _context.Logs.Add(new Log
        {
            UserId = 1,
            VisitedEndpoint = "/api/teste/{id}",
            Created = Stored(created)
        });
    }

    private static DateTime Stored(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
        }
    }
}
