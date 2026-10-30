using GitDashBackend.Configurations;
using GitDashBackend.Middlewares;
using GitDashBackend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 1. Get the connection string
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// 2. Add the DbContext to the services
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString)); // <-- Tells EF Core to use Npgsql


// Configure Logging with timestamp
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    options.UseUtcTimestamp = false;
});


// Add services to the container.
builder.Services.AddControllers();


// === GitHub OAuth Authentication ===
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "GitHub";
    })
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    })
    .AddOAuth("GitHub", options =>
    {
        options.ClientId = builder.Configuration["GitHub:ClientId"];
        options.ClientSecret = builder.Configuration["GitHub:ClientSecret"];

        // This must match the callback URL in the GitHub OAuth App
        options.CallbackPath = new PathString("/signin-github");

        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";

        options.SaveTokens = true;
        options.Scope.Add("repo");

        options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "login");
        options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {

                // Load GitHub user info
                var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("GitDashBackend"); // GitHub requires User-Agent
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

                var response = await context.Backchannel.SendAsync(request);
                response.EnsureSuccessStatusCode();

                using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                context.RunClaimActions(user.RootElement);

                if (!string.IsNullOrEmpty(context.AccessToken))
                {
                    context.Identity!.AddClaim(
                        new System.Security.Claims.Claim("github_access_token", context.AccessToken));
                }
            }
        };
    });
    



// Configure Redis Cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration["Redis:Configuration"];
    options.InstanceName = builder.Configuration["Redis:InstanceName"];
});

// Configure GitHub Settings
builder.Services.Configure<GitHubSettings>(
    builder.Configuration.GetSection("GitHub"));

// OpenAPI
builder.Services.AddOpenApi();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "GitDash Backend API",
        Version = "v1",
        Description = "API for GitDash - GitHub Dashboard. \n\n" +
                      "**Auth Instructions:** \n" +
                      "1. Go to `/login` in your browser to sign in via GitHub.\n" +
                      "2. Come back here and execute endpoints. The browser handles the cookie automatically.",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "GitDash Team - ADS-Grupo_PosLaboral-A",
            Url = new Uri("https://github.com/acavieira/ADS-Grupo_PosLaboral-A")
        }
    });

    // REMOVED: AddSecurityDefinition ("GitHubToken")
    // REMOVED: AddSecurityRequirement
});

// Register custom services 
builder.Services.AddOptions<RetentionSettings>()
    .Bind(builder.Configuration.GetSection("Retention"))
    .Validate(settings => settings.HistoryDays > 0 && settings.LogDays > 0,
        "Retention:HistoryDays and Retention:LogDays must be positive.")
    .ValidateOnStart();

builder.Services.AddProjectServices();

// Add CORS for frontend
var frontendOrigin = builder.Configuration["FrontendOrigin"]
                     ?? "http://localhost:5173";

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(frontendOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "GitDash Backend API v1");
        options.RoutePrefix = "swagger";
    });
}

//app.UseHttpsRedirection();

app.UseCors("Frontend");

// Middleware to log access to endpoints
((IApplicationBuilder)app).UseAccessLog();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
