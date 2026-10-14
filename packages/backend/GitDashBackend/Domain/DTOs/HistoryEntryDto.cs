namespace GitDashBackend.Domain.DTOs;

/// <summary>One entry of the session user's repository history. Carries no user identifiers.</summary>
public record HistoryEntryDto(string Repository, DateTime LastVisited, int Visits);
