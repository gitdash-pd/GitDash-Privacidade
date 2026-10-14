using System.Security.Claims;
using GitDashBackend.Extensions;

namespace GitDashBackend.Configurations;

public static class AuthorizationPolicies
{
    public const string Admin = "Admin";

    /// <summary>
    /// Registers the Admin policy. Only the GitHub logins listed in Admin:Logins can use general listings.
    /// An empty list means nobody is an administrator.
    /// </summary>
    public static IServiceCollection AddGitDashAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        var adminLogins = ReadAdminLogins(configuration);

        services.AddAuthorization(options =>
            options.AddPolicy(Admin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsAdmin(context.User, adminLogins))));

        return services;
    }

    public static HashSet<string> ReadAdminLogins(IConfiguration configuration) =>
        (configuration.GetSection("Admin:Logins").Get<string[]>() ?? Array.Empty<string>())
            .Where(login => !string.IsNullOrWhiteSpace(login))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAdmin(ClaimsPrincipal user, IReadOnlySet<string> adminLogins)
    {
        var login = user.GetGitHubLogin();
        return !string.IsNullOrEmpty(login) && adminLogins.Contains(login);
    }
}
