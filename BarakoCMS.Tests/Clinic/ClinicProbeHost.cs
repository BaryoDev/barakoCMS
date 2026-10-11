using barakoCMS.Models;
using BarakoCMS.Testing;
using Marten;

namespace BarakoCMS.Tests.Clinic;

public sealed class ClinicProbeHost : BarakoTestHost
{
    public ClinicProbeHost() : base(new ClinicProbeModule()) { }

    public sealed record ProbeCaller(HttpClient Client, Guid RoleId, string RoleName)
    {
        /// <summary>The bearer token the client sends, for a SignalR connection.</summary>
        public string Token => Client.DefaultRequestHeaders.Authorization!.Parameter!;
    }

    /// <summary>
    /// A new user whose only role holds exactly <paramref name="capabilities"/>. The role is
    /// stored directly, so a test can grant a name before any vocabulary lists it. The role id and
    /// name come back so a test can change the role through PUT /api/roles/{id}.
    /// </summary>
    public async Task<ProbeCaller> CallerHoldingAsync(string[] capabilities, string? tenantSlug = null)
    {
        var roleId = Guid.NewGuid();
        var roleName = $"probe-{Guid.NewGuid():N}"[..20];
        await using (var session = OpenSession())
        {
            session.Store(new Role
            {
                Id = roleId,
                Name = roleName,
                SystemCapabilities = capabilities.ToList(),
            });
            await session.SaveChangesAsync();
        }

        var client = tenantSlug is null
            ? await CreateClientAsync([roleName])
            : await CreateClientAsync([roleName], tenantSlug);

        return new ProbeCaller(client, roleId, roleName);
    }

    public async Task<HttpClient> ClientHoldingAsync(string[] capabilities, string? tenantSlug = null) =>
        (await CallerHoldingAsync(capabilities, tenantSlug)).Client;

    /// <summary>Appends one event to a stream (starting it when new) and returns its sequence.</summary>
    public async Task<long> AppendAsync(object @event, Guid streamId, string? tenantSlug = null)
    {
        await using var session = OpenSession(tenantSlug);
        var state = await session.Events.FetchStreamStateAsync(streamId);
        if (state is null)
            session.Events.StartStream(streamId, @event);
        else
            session.Events.Append(streamId, @event);
        await session.SaveChangesAsync();

        var events = await session.Events.FetchStreamAsync(streamId);
        return events[^1].Sequence;
    }
}
