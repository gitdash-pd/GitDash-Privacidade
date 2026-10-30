using System.Security.Claims;
using GitDashBackend.Configurations;
using GitDashBackend.Controllers;
using GitDashBackend.Data;
using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;
using GitDashBackend.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GitDashBackend.Tests.Controllers
{
    // R1: each person only reads their own history and access logs
    public class HistoryControllerTests
    {
        private static AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        private static async Task SeedAsync(AppDbContext context)
        {
            var ana = new User { Username = "ana", Created = DateTime.UtcNow };
            var bruno = new User { Username = "bruno", Created = DateTime.UtcNow };
            context.Users.AddRange(ana, bruno);
            await context.SaveChangesAsync();

            var now = DateTime.UtcNow;
            context.Repositories.AddRange(
                new Repository { UserId = ana.Id, Name = "ana/app", LastVisited = now, VisitsNumber = 2, Created = now },
                new Repository { UserId = bruno.Id, Name = "bruno/private", LastVisited = now, VisitsNumber = 5, Created = now });
            context.Logs.AddRange(
                new Log { UserId = ana.Id, VisitedEndpoint = "/api/github/stats", Created = now },
                new Log { UserId = bruno.Id, VisitedEndpoint = "/api/github/collaborators", Created = now });
            await context.SaveChangesAsync();
        }

        // login == null simulates a request without a session
        private static HistoryController CreateController(AppDbContext context, string? login)
        {
            var identity = login == null
                ? new ClaimsIdentity()
                : new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, login) }, "Test");

            return new HistoryController(new DbService(context))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
                }
            };
        }

        [Fact]
        public async Task GetHistory_ReturnsOnlyEntriesOfSessionUser()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, "ana");

            var result = await controller.GetHistory();

            var ok = Assert.IsType<OkObjectResult>(result);
            var entries = Assert.IsAssignableFrom<IEnumerable<HistoryEntryDto>>(ok.Value);
            var entry = Assert.Single(entries);
            Assert.Equal("ana/app", entry.Repository);
        }

        [Fact]
        public async Task GetHistory_ReturnsUnauthorized_WithoutSession()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, null);

            var result = await controller.GetHistory();

            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetHistory_ReturnsUnauthorized_WhenSessionUserIsUnknown()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, "carla");

            var result = await controller.GetHistory();

            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetLogs_ReturnsOnlyLogsOfSessionUser()
        {
            using var context = CreateContext();
            await SeedAsync(context);
            var controller = CreateController(context, "bruno");

            var result = await controller.GetLogs();

            var ok = Assert.IsType<OkObjectResult>(result);
            var logs = Assert.IsAssignableFrom<IEnumerable<AccessLogDto>>(ok.Value);
            var log = Assert.Single(logs);
            Assert.Equal("/api/github/collaborators", log.Endpoint);
        }

        [Fact]
        public void HistoryController_RequiresAuthenticatedSession()
        {
            var attributes = typeof(HistoryController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

            Assert.NotEmpty(attributes);
        }

        [Fact]
        public void AdminController_RequiresAdminPolicy()
        {
            var attribute = Assert.Single(
                typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));

            Assert.Equal(AuthorizationPolicies.Admin, ((AuthorizeAttribute)attribute).Policy);
        }
    }
}
