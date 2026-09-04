namespace MyBarakoModule;

/// <summary>
/// Bound from <c>Modules:ModuleName</c>. As an environment variable a key here is
/// <c>Modules__ModuleName__Greeting</c>.
/// </summary>
public sealed class ModuleNameOptions
{
    public string Greeting { get; set; } = "Hello from ModuleName";
}
