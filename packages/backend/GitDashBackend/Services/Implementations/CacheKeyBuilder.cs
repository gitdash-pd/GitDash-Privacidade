using System.Security.Cryptography;
using System.Text;

namespace GitDashBackend.Services.Implementations;

/// <summary>
/// Builds Redis keys bound to the session's GitHub token without storing the token or readable names.
/// SHA-256 is stable across restarts (string.GetHashCode is randomised per process) and collision-resistant,
/// so two sessions never share an entry.
/// </summary>
public static class CacheKeyBuilder
{
    public static string Build(string kind, string token, params string[] parts)
    {
        var material = string.Join('\n', new[] { kind, token }.Concat(parts));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"{kind}:{hash}";
    }
}
