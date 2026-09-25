using barakoCMS.Models;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Barako.Fixture.Legacy42;

// A module as a third party built one against BarakoCMS 4.2.0: every barakoCMS type it names is
// recorded against assembly barakoCMS. It touches the module contract, a model, an enum and a
// generic, the shapes the forwarders in barakoCMS/TypeForwards.cs have to carry.
public sealed class Legacy42Module : IBarakoModule
{
    public string Name => "Legacy42Fixture";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new Legacy42Marker(
            ContentStatus.Published,
            new PaginatedResponse<Content> { Items = [new Content { ContentType = "legacy42" }], Page = 1, PageSize = 1, TotalItems = 1 }));
}

public sealed record Legacy42Marker(ContentStatus Status, PaginatedResponse<Content> Page);
