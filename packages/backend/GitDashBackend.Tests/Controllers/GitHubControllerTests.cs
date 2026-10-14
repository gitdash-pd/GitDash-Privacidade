using System.Security.Claims;
using GitDashBackend.Controllers;
using GitDashBackend.Data;
using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;
using GitDashBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;


namespace GitDashBackend.Tests.Controllers
{
    // Simple implementation for the scenario where there is no token (NoResult)
    internal class FakeAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            return Task.CompletedTask;
        }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            return Task.CompletedTask;
        }
    }

    public class GitHubControllerTests
    {
        // Creates an in-memory database context for isolated tests
        private AppDbContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        // Helper to create a controller instance with mocks and context
        private GitHubController CreateController(
            AppDbContext context,
            Mock<IGitHubService>? gitHubServiceMock = null,
            Mock<IDbService>? dbServiceMock = null)
        {
            var gitHubService = gitHubServiceMock?.Object ?? Mock.Of<IGitHubService>();
            var dbService = dbServiceMock?.Object ?? Mock.Of<IDbService>();
            var logger = Mock.Of<ILogger<GitHubController>>();

            var controller = new GitHubController(
                gitHubService,
                dbService,
                logger,
                context
            );

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            return controller;
        }

        // Authenticated session with a login claim and a GitHub token, as the cookie middleware would provide
        private static DefaultHttpContext CreateAuthenticatedContext(string login, string token)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, login)
            }, "Test"));

            var props = new AuthenticationProperties();
            props.StoreTokens(new[] { new AuthenticationToken { Name = "access_token", Value = token } });

            var authServiceMock = new Mock<IAuthenticationService>();
            authServiceMock
                .Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string?>()))
                .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(principal, props, "Test")));

            var services = new ServiceCollection();
            services.AddSingleton(authServiceMock.Object);

            return new DefaultHttpContext
            {
                User = principal,
                RequestServices = services.BuildServiceProvider()
            };
        }

        [Fact]
        public async Task GetRepositoryCommits_ReturnsUnauthorized_WhenSessionHasNoToken()
        {
            using var context = CreateInMemoryContext();
            var controller = CreateController(context);

            var httpContext = new DefaultHttpContext();
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService, FakeAuthenticationService>();
            httpContext.RequestServices = services.BuildServiceProvider();
            controller.ControllerContext.HttpContext = httpContext;

            // The token now comes from the session, never from a request header
            var result = await controller.GetRepositoryCommits("owner/repo");

            var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        }

        [Fact]
        public async Task GetRepositories_DoesNotChangeHistoryOfOtherUsers()
        {
            using var context = CreateInMemoryContext();
            var ana = new User { Username = "ana", Created = DateTime.UtcNow };
            var bruno = new User { Username = "bruno", Created = DateTime.UtcNow };
            context.Users.AddRange(ana, bruno);
            await context.SaveChangesAsync();

            // Bruno visited a repository that Ana can also list
            var lastVisited = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);
            context.Repositories.Add(new Repository
            {
                UserId = bruno.Id,
                Name = "ana/shared",
                LastVisited = lastVisited,
                VisitsNumber = 1,
                Created = lastVisited
            });
            await context.SaveChangesAsync();

            var gitHubServiceMock = new Mock<IGitHubService>();
            gitHubServiceMock
                .Setup(s => s.GetUserRepositoriesAsync("token-ana"))
                .ReturnsAsync(new RepositoriesDto
                {
                    count = 1,
                    repositories = { new RepositoryDto { FullName = "ana/shared" } }
                });

            var controller = CreateController(context, gitHubServiceMock);
            controller.ControllerContext.HttpContext = CreateAuthenticatedContext("ana", "token-ana");

            var result = await controller.GetRepositories();

            Assert.IsType<OkObjectResult>(result);
            var brunoEntry = context.Repositories.AsNoTracking().Single(r => r.UserId == bruno.Id);
            Assert.Equal(lastVisited, brunoEntry.LastVisited);
            Assert.Equal(1, brunoEntry.VisitsNumber);
        }

        [Fact]
        public async Task GetCollaboratorCodeChanges_ReturnsUnauthorized_WhenNoAccessToken()
        {
            using var context = CreateInMemoryContext();
            var gitHubServiceMock = new Mock<IGitHubService>();
            var controller = CreateController(context, gitHubServiceMock);

            var httpContext = new DefaultHttpContext();

            // ServiceProvider with IAuthenticationService that does not return a token (NoResult)
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService, FakeAuthenticationService>();
            httpContext.RequestServices = services.BuildServiceProvider();

            controller.ControllerContext.HttpContext = httpContext;

            var result = await controller.GetCollaboratorCodeChanges("owner/repo", "ana", "1m");

            var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        }

        [Fact]
        public async Task GetRepositoryByUrl_ReturnsBadRequest_ForInvalidUrl()
        {
            using var context = CreateInMemoryContext();
            var gitHubServiceMock = new Mock<IGitHubService>();
            var controller = CreateController(context, gitHubServiceMock);

            var httpContext = new DefaultHttpContext();

            // Aqui queremos passar a validação do token -> configurar AuthenticateAsync para devolver token "access_token"
            var authServiceMock = new Mock<IAuthenticationService>();

            var claimsIdentity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "ana")
            }, "Test");

            var principal = new ClaimsPrincipal(claimsIdentity);

            var props = new AuthenticationProperties();
            props.StoreTokens(new[]
            {
                new AuthenticationToken
                {
                    Name = "access_token",
                    Value = "token123"
                }
            });

            var ticket = new AuthenticationTicket(principal, props, "TestScheme");
            var authResult = AuthenticateResult.Success(ticket);

            authServiceMock
                .Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string?>()))
                .ReturnsAsync(authResult);

            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(authServiceMock.Object);
            httpContext.RequestServices = services.BuildServiceProvider();

            controller.ControllerContext.HttpContext = httpContext;

            // URL inválido -> depois de passar o token, deve cair na validação do URL
            var result = await controller.GetRepositoryByUrl("isto-nao-e-url");

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        }
    }
}
