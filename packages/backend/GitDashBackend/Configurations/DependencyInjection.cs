﻿using GitDashBackend.Services;

namespace GitDashBackend.Configurations;

using GitDashBackend.Accessors.Implementations;
using GitDashBackend.Accessors.Interfaces;
using GitDashBackend.Services.Implementations;
using GitDashBackend.Services.Interfaces;

public static class DependencyInjection
{
    public static IServiceCollection AddProjectServices(this IServiceCollection services)
    {
        // Register Accessors (Infrastructure Layer)
        services.AddScoped<IGitHubAccessor, GitHubAccessor>();
        services.AddScoped<IRedisAccessor, RedisAccessor>();

        // Register Services (Application Layer)
        services.AddScoped<IGitHubService, GitHubService>();
        services.AddScoped<IDbService, DbService>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionWorker>();

        return services;
    }
}
