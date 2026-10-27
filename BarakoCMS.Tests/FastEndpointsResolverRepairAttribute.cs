using System.Reflection;
using Xunit.v3;

[assembly: BarakoCMS.Tests.RepairFastEndpointsResolver]

namespace BarakoCMS.Tests;

/// <summary>
/// Before every test, moves FastEndpoints' static resolver off a disposed host (#554).
/// </summary>
/// <remarks>
/// Many classes build a host of their own (<c>BarakoTestHost</c>, <c>WebApplication.CreateBuilder</c>,
/// a bare factory) and dispose it when the class is done. Each leaves the process-wide resolver on
/// that disposed host, and the next test that goes through it outside a request, such as
/// <c>PreviewToken.Create</c> calling <c>JwtBearer.CreateToken</c>, throws
/// <see cref="ObjectDisposedException"/>. Whether that shows depends on which classes ran before it,
/// which is why it passed on one machine and failed in CI shards. Repairing here covers every such
/// class instead of asking each to take <see cref="FastEndpointsResolver.Keep"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RepairFastEndpointsResolverAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        FastEndpointsResolver.RepairQuietly();
}
