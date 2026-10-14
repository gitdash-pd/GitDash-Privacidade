namespace GitDashBackend.Configurations;

/// <summary>
/// Cache durations in minutes, bound from the "Cache" section.
/// </summary>
public class CacheSettings
{
    /// <summary>How long a GitHub response is reused (report, section 4.2: 30 minutes).</summary>
    public double TtlMinutes { get; set; } = 30;

    /// <summary>
    /// How long a confirmed access to a repository is trusted before asking GitHub again.
    /// This is the longest time a cached response can still be served after the access is revoked. 0 = always ask.
    /// </summary>
    public double AccessCheckMinutes { get; set; } = 5;
}
