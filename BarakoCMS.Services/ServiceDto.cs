namespace BarakoCMS.Services;

internal sealed class ServiceDto
{
    public string Name { get; set; } = string.Empty;
    public List<string> MessageTypes { get; set; } = new();
    public string? DeliveryUrl { get; set; }
    public string? Topic { get; set; }
    public string? HealthUrl { get; set; }
    public Guid? MachineKeyId { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>Healthy, Late or Down.</summary>
    public string Health { get; set; } = string.Empty;

    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ServiceDto From(ServiceRegistration s, DateTime now) => new()
    {
        Name = s.Name,
        MessageTypes = s.MessageTypes,
        DeliveryUrl = s.DeliveryUrl,
        Topic = s.Topic,
        HealthUrl = s.HealthUrl,
        MachineKeyId = s.MachineKeyId,
        HeartbeatIntervalSeconds = s.HeartbeatIntervalSeconds,
        Health = ServiceHealth.Evaluate(s, now).ToString(),
        LastHeartbeatAt = s.LastHeartbeatAt,
        CreatedAt = s.CreatedAt,
    };
}
