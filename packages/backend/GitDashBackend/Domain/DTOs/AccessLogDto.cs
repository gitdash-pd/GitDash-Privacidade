namespace GitDashBackend.Domain.DTOs;

/// <summary>Access log entry of the session user.</summary>
public record AccessLogDto(string Endpoint, DateTime Created);

/// <summary>Access log entry shown to administrators, who need to know which account made the request.</summary>
public record AdminAccessLogDto(string Username, string Endpoint, DateTime Created);
