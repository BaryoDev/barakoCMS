using Marten.Schema;

namespace barakoCMS.Models;

public class IdempotencyRecord
{
    [Identity]
    public string Key { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
