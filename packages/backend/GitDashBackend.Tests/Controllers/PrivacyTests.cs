using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using GitDashBackend.Accessors.Interfaces;
using GitDashBackend.Data;
using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace GitDashBackend.Tests.Controllers;

public class PrivacyTests
{
    private const string Login = "conta-a";
    private const string Token = "token-ficticio";
    private const string Repo = "equipa/repositorio";
    private const string WeeklyUrl = "/api/github/repositories/equipa%2Frepositorio/collaborators/terceiro/weekly-activity";

    [Fact]
    public async Task AccessLog_StoresOneRouteTemplateWithoutRequestValues()
    {
        using var app = new TestApplication();
        using var client = CreateClient(app);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer cabecalho-ficticio");

        var response = await client.GetAsync(WeeklyUrl + "?token=segredo-na-query");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = app.Services.CreateScope();
        var log = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Logs.SingleAsync();
        Assert.Equal("/api/GitHub/repositories/{fullName}/collaborators/{username}/weekly-activity", log.VisitedEndpoint);
        Assert.Equal(1, log.UserId);
        var messages = app.Logs.Messages.Where(m => m.Level == LogLevel.Information).ToList();
        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            Assert.DoesNotContain("equipa", message.Text);
            Assert.DoesNotContain("terceiro", message.Text);
            Assert.DoesNotContain(Token, message.Text);
            Assert.DoesNotContain("segredo-na-query", message.Text);
            Assert.DoesNotContain("cabecalho-ficticio", message.Text);
        }
    }

    [Theory]
    [InlineData(null, WeeklyUrl, HttpStatusCode.Unauthorized)]
    [InlineData("conta-desconhecida", WeeklyUrl, HttpStatusCode.OK)]
    [InlineData(Login, "/sem-rota/terceiro", HttpStatusCode.NotFound)]
    public async Task AccessLog_SkipsRequestsWithoutAnAuthorizedLocalUserOrRoute(
        string? login, string url, HttpStatusCode expected)
    {
        using var app = new TestApplication();
        using var client = CreateClient(app, login);

        var response = await client.GetAsync(url);

        Assert.Equal(expected, response.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Logs.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commits_DoNotReturnEmailEvenFromOldCacheEntries(bool cached)
    {
        using var app = new TestApplication();
        app.GitHub.Setup(a => a.GetRepositoryCommitsByFullNameAsync(Token, Repo))
            .ReturnsAsync(new CommitsDto
            {
                count = 1,
                commits = { new CommitDto { Message = "Atualizar projeto", AuthorName = "Autor de teste" } }
            });
        using var client = CreateClient(app);
        if (cached)
        {
            var key = $"commits_{Repo.Replace("/", "_")}_{Token.GetHashCode()}";
            await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync(key,
                """{"count":1,"commits":[{"Message":"Atualizar projeto","AuthorName":"Autor de teste","AuthorEmail":"autor@example.test"}]}""");
        }

        var response = await client.GetAsync("/api/github/commits?fullName=" + Uri.EscapeDataString(Repo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var commit = Assert.Single(json.RootElement.GetProperty("commits").EnumerateArray());
        Assert.Equal("Atualizar projeto", commit.GetProperty("message").GetString());
        Assert.False(commit.TryGetProperty("authorEmail", out _));
        Assert.DoesNotContain("autor@example.test", body);
    }

    [Fact]
    public async Task Overview_StillUpdatesOnlyTheSessionUsersHistory()
    {
        using var app = new TestApplication();
        app.GitHub.Setup(a => a.GetRepositoryStatsAsync(Token, Repo, "1 week"))
            .ReturnsAsync(new RepoOverviewStatsDto());
        using var client = CreateClient(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User { Id = 2, Username = "conta-b", Created = DateTime.UtcNow });
        db.Repositories.Add(new Repository
        {
            UserId = 2, Name = Repo, VisitsNumber = 7,
            LastVisited = DateTime.UtcNow.AddDays(-1), Created = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync("/api/github/stats/equipa%2Frepositorio/1%20week");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await db.Repositories.SingleAsync(r => r.UserId == 1)).VisitsNumber);
        Assert.Equal(7, (await db.Repositories.SingleAsync(r => r.UserId == 2)).VisitsNumber);
        Assert.Single(await db.Logs.ToListAsync());
    }

    [Fact]
    public async Task OAuth_KeepsSavedTokenWithoutAddingADuplicateClaim()
    {
        using var app = new TestApplication();
        using var client = CreateClient(app);
        var options = app.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");
        var identity = new ClaimsIdentity("GitHub");
        using var token = OAuthTokenResponse.Success(JsonDocument.Parse("""{"access_token":"token-ficticio"}"""));
        using var user = JsonDocument.Parse("{}");
        using var backchannel = new HttpClient(new UserInfoHandler());
        var ticket = new OAuthCreatingTicketContext(new ClaimsPrincipal(identity), new AuthenticationProperties(),
            new DefaultHttpContext(), new AuthenticationScheme("GitHub", null, typeof(OAuthHandler<OAuthOptions>)),
            options, backchannel, token, user.RootElement);

        await options.Events.OnCreatingTicket(ticket);

        Assert.True(options.SaveTokens);
        Assert.Equal(Login, identity.FindFirst(ClaimTypes.Name)?.Value);
        Assert.DoesNotContain(identity.Claims, c => c.Type == "github_access_token" || c.Value == Token);
    }

    private static HttpClient CreateClient(TestApplication app, string? login = Login)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User { Id = 1, Username = Login, Created = DateTime.UtcNow });
        db.SaveChanges();
        if (login != null)
        {
            var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, login) }, CookieAuthenticationDefaults.AuthenticationScheme));
            var properties = new AuthenticationProperties();
            properties.StoreTokens(new[] { new AuthenticationToken { Name = "access_token", Value = Token } });
            var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
            client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
        }
        return client;
    }

    private sealed class TestApplication : WebApplicationFactory<Program>
    {
        public Mock<IGitHubAccessor> GitHub { get; } = new();
        public TestLogger Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var databaseName = Guid.NewGuid().ToString();
            builder.UseSetting("GitHub:ClientId", "cliente-ficticio");
            builder.UseSetting("GitHub:ClientSecret", "segredo-ficticio");
            builder.UseSetting("Retention:RunOnStartup", "false");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();
                services.RemoveAll<IGitHubAccessor>();
                services.AddSingleton(GitHub.Object);
            });
            GitHub.Setup(a => a.GetCollaboratorWeeklyActivityAsync(Token, Repo, "terceiro"))
                .ReturnsAsync(new List<int> { 1 });
        }
    }

    private sealed class TestLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class UserInfoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":1,"login":"conta-a"}""")
            });
    }
}
