using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;
using GitDashBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GitDashBackend.Services.Implementations;

public class DbService : IDbService
{
    private readonly Data.AppDbContext _dbContext;
    
    public DbService(Data.AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }
    
    public async Task<User?> GetUserbyUsername(string username)
    {
        return await _dbContext.Users.SingleOrDefaultAsync(u => u.Username == username);
    }

    // Upsert Repository
    public async Task UpsertVisitedRepository(String decodedFullName, int userId)
    {
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        
        // Check if this specific user already has this repository in the DB
        var existingRepo = await _dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Name == decodedFullName && r.UserId == userId);

        if (existingRepo != null)
        {
            // UPDATE: Repo exists, just update the timestamp and visitnumber
            existingRepo.LastVisited = now;
            existingRepo.VisitsNumber = ++existingRepo.VisitsNumber;
        }
        else
        {
            // INSERT: Repo does not exist, create new entry
            var newRepo = new Models.Repository
            {
                Name = decodedFullName, // Assuming the column is 'Name' or 'FullName'
                UserId = userId,
                LastVisited = now,
                VisitsNumber = 1,
                Created = now
            };
            _dbContext.Repositories.Add(newRepo);
            
        }
        
        await _dbContext.SaveChangesAsync();
    }

    public async Task InsertNewLog(int userId, string visitedEndpoint)
    {
        _dbContext.Logs.Add(new Models.Log
        {
            UserId = userId,
            VisitedEndpoint = visitedEndpoint,
            Created = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        });
        
        await _dbContext.SaveChangesAsync();
    }
    
    public async Task<List<Repository>> GetRecentRepositories(string username)
    {
        return await _dbContext.Repositories
            .AsNoTracking()
            .Include(r => r.User)
            .Where(r => r.User.Username == username)
            .OrderByDescending(r => r.LastVisited)
            .Take(3)
            .ToListAsync();
    }

    public async Task<List<HistoryEntryDto>> GetHistoryAsync(int userId)
    {
        return await _dbContext.Repositories
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.LastVisited)
            .Select(r => new HistoryEntryDto(r.Name, r.LastVisited, r.VisitsNumber))
            .ToListAsync();
    }

    public async Task<List<AccessLogDto>> GetAccessLogsAsync(int userId)
    {
        return await _dbContext.Logs
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.Created)
            .Select(l => new AccessLogDto(l.VisitedEndpoint, l.Created))
            .ToListAsync();
    }

    public async Task<List<AdminAccessLogDto>> GetRecentAccessLogsAsync(int limit)
    {
        return await _dbContext.Logs
            .AsNoTracking()
            .OrderByDescending(l => l.Created)
            .Take(limit)
            .Select(l => new AdminAccessLogDto(l.User.Username, l.VisitedEndpoint, l.Created))
            .ToListAsync();
    }
}