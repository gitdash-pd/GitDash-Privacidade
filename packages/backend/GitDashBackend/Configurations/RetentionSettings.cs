namespace GitDashBackend.Configurations;

public class RetentionSettings
{
    public int HistoryDays { get; set; } = 90;
    public int LogDays { get; set; } = 180;
    public bool RunOnStartup { get; set; } = true;
}
