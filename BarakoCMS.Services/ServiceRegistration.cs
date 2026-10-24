namespace BarakoCMS.Services;

/// <summary>
/// An outside service barakoCMS sends messages to or hears from. One row per name, deployment-wide.
/// </summary>
public class ServiceRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lower case, unique, and the route segment the service reports its heartbeat under.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The message types the service handles.</summary>
    public List<string> MessageTypes { get; set; } = new();

    /// <summary>Where messages are posted. One of this and <see cref="Topic"/> is always set.</summary>
    public string? DeliveryUrl { get; set; }

    /// <summary>The transport topic messages are published to, for a service reached that way.</summary>
    public string? Topic { get; set; }

    /// <summary>Where barakoCMS can poll for health. Optional.</summary>
    public string? HealthUrl { get; set; }

    /// <summary>The machine key the service writes back with. A reference only, never the secret.</summary>
    public Guid? MachineKeyId { get; set; }

    /// <summary>How often the service promises to report. <see cref="ServiceHealth"/> reads from it.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = ServiceHealth.DefaultIntervalSeconds;

    public DateTime? LastHeartbeatAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
