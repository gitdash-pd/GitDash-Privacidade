namespace GitDashBackend.Domain.DTOs;

public class CommitDto
{
    public string Message { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int TotalChanges { get; set; }
}
