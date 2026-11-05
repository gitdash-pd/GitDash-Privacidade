using GitDashBackend.Extensions;
using GitDashBackend.Services.Interfaces;

namespace GitDashBackend.Middlewares;

public class AccessLogMiddleware
{
    private readonly RequestDelegate _next;

    public AccessLogMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IDbService dbService)
    {
        var username = context.User.GetGitHubLogin();
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        if (context.User.Identity?.IsAuthenticated == true &&
            !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(route))
        {
            var user = await dbService.GetUserbyUsername(username);
            if (user != null)
                await dbService.InsertNewLog(user.Id, "/" + route.TrimStart('/'));
        }

        await _next(context);
    }
}

// Extension to register the middleware
public static class AccessLogMiddlewareExtensions
{
    public static IApplicationBuilder UseAccessLog(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AccessLogMiddleware>();
    }
}
