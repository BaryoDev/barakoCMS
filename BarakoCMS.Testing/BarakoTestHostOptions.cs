using barakoCMS.Modules;

namespace BarakoCMS.Testing;

/// <summary>
/// What a <see cref="BarakoTestHost"/> runs: the modules under test, the settings they read, and the
/// admin account the host seeds.
/// </summary>
public sealed class BarakoTestHostOptions
{
    /// <summary>The modules the host registers, in this order. Discovery is off, so this is the whole list.</summary>
    public IList<IBarakoModule> Modules { get; } = [];

    /// <summary>
    /// Configuration keys layered over the host's own defaults, so a module's section can be set from
    /// the test: <c>Settings["Modules:MyModule:ApiKey"] = "..."</c>.
    /// </summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The seeded admin. Holds the SuperAdmin and Admin roles, as <c>InitialAdmin</c> does on a real host.</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// The seeded admin's password, for a test that signs in through <c>POST /api/auth/login</c>.
    /// Fresh per host so a value copied out of a test never opens anything else.
    /// </summary>
    public string AdminPassword { get; set; } = $"Test-{Guid.NewGuid():N}!";

    /// <summary>The PostgreSQL image Testcontainers starts.</summary>
    public string PostgresImage { get; set; } = "postgres:16-alpine";

    /// <summary>
    /// The host environment. Development keeps Swagger on and lets Marten update an existing
    /// schema, which is what a throwaway database wants.
    /// </summary>
    public string EnvironmentName { get; set; } = "Development";
}
