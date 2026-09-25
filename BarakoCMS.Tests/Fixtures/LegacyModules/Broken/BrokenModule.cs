using barakoCMS.Modules;

namespace Barako.Fixture.Broken;

// The module type itself loads: IBarakoModule is forwarded to BarakoCMS.Abstractions. The type
// below it does not, because its base type exists in no barakoCMS. Discovery must skip the whole
// assembly, not register the module and leave the endpoint scan to fall over the other type.
public sealed class BrokenFixtureModule : IBarakoModule
{
    public string Name => "BrokenFixture";
}

public class LeansOnAMissingType : barakoCMS.Models.NeverShipped
{
}
