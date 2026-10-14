using System.Security.Claims;

namespace GitDashBackend.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// GitHub login confirmed at sign-in and kept in the encrypted session cookie.
    /// This is the only identity used to select or change a user's data; names sent in the request are never trusted.
    /// </summary>
    public static string? GetGitHubLogin(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Name)?.Value;
}
