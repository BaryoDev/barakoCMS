namespace barakoCMS.Models;

public class SystemSetting
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SettingCategory Category { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum SettingCategory
{
    Features,
    Logging,
    Monitoring,
    System
}
