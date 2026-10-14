using System;
using System.Linq;
using System.Threading.Tasks;
using GitDashBackend.Data;
using GitDashBackend.Models;
using GitDashBackend.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitDashBackend.Tests.Services
{
    public class DbServiceTests
    {
        private AppDbContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new AppDbContext(options);
        }

        [Fact]
        public async Task GetUserByUsername_ReturnsUser_WhenExists()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            context.Users.Add(new User
            {
                Username = "ana",
                Created = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            var user = await service.GetUserbyUsername("ana");

            Assert.NotNull(user);
            Assert.Equal("ana", user!.Username);
        }

        [Fact]
        public async Task UpsertVisitedRepository_Inserts_WhenNotExists()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            var user = new User
            {
                Username = "ana",
                Created = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            await service.UpsertVisitedRepository("owner/repo", user.Id);

            var repo = context.Repositories.SingleOrDefault(r => r.Name == "owner/repo" && r.UserId == user.Id);
            Assert.NotNull(repo);
            Assert.Equal(1, repo!.VisitsNumber);
            Assert.NotEqual(default, repo.Created);
            Assert.NotEqual(default, repo.LastVisited);
        }

        [Fact]
        public async Task UpsertVisitedRepository_Updates_WhenExists()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            var user = new User
            {
                Username = "ana",
                Created = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var repo = new Repository
            {
                Name = "owner/repo",
                UserId = user.Id,
                VisitsNumber = 1,
                Created = DateTime.UtcNow.AddHours(-1),
                LastVisited = DateTime.UtcNow.AddHours(-1)
            };
            context.Repositories.Add(repo);
            await context.SaveChangesAsync();

            var oldVisits = repo.VisitsNumber;
            var oldLastVisited = repo.LastVisited;

            await service.UpsertVisitedRepository("owner/repo", user.Id);

            var updated = context.Repositories.Single(r => r.Id == repo.Id);

            Assert.Equal(oldVisits + 1, updated.VisitsNumber);
            Assert.NotEqual(oldLastVisited, updated.LastVisited);
        }

        [Fact]
        public async Task InsertNewLog_AddsLogEntry()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            var user = new User
            {
                Username = "ana",
                Created = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            await service.InsertNewLog(user.Id, "/api/github/repositories");

            var log = context.Logs.SingleOrDefault();
            Assert.NotNull(log);
            Assert.Equal(user.Id, log!.UserId);
            Assert.Equal("/api/github/repositories", log.VisitedEndpoint);
        }

        [Fact]
        public async Task GetRecentRepositories_ReturnsAtMost3Ordered()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            var user = new User
            {
                Username = "ana",
                Created = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            for (int i = 0; i < 5; i++)
            {
                context.Repositories.Add(new Repository
                {
                    Name = $"owner/repo{i}",
                    UserId = user.Id,
                    LastVisited = DateTime.UtcNow.AddMinutes(-i),
                    VisitsNumber = i + 1,
                    Created = DateTime.UtcNow.AddMinutes(-i)
                });
            }

            await context.SaveChangesAsync();

            var recent = await service.GetRecentRepositories("ana");

            Assert.Equal(3, recent.Count);
            Assert.True(recent[0].LastVisited >= recent[1].LastVisited);
            Assert.True(recent[1].LastVisited >= recent[2].LastVisited);
        }

        [Fact]
        public async Task GetRecentAccessLogsAsync_RespectsLimitAndOrder()
        {
            using var context = CreateInMemoryContext();
            var service = new DbService(context);

            var user = new User { Username = "ana", Created = DateTime.UtcNow };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var now = DateTime.UtcNow;
            for (int i = 0; i < 4; i++)
            {
                context.Logs.Add(new Log
                {
                    UserId = user.Id,
                    VisitedEndpoint = $"/api/endpoint{i}",
                    Created = now.AddMinutes(-i)
                });
            }
            await context.SaveChangesAsync();

            var logs = await service.GetRecentAccessLogsAsync(2);

            Assert.Equal(2, logs.Count);
            Assert.Equal("/api/endpoint0", logs[0].Endpoint);
            Assert.Equal("ana", logs[0].Username);
        }
    }
}
