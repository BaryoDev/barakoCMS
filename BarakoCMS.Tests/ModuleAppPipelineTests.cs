using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// How <c>IBarakoModule.ConfigureApp</c> is called and composed, on a bare pipeline with no host.
/// Where the result sits among core's middleware is <see cref="ModuleConfigureAppTests"/>.
/// </summary>
public class ModuleAppPipelineTests
{
    private const string Header = "X-Module-Order";
    private const string Reached = "reached";

    private sealed class Plain(string name) : IBarakoModule
    {
        public string Name { get; } = name;
    }

    private sealed class Stamping(string name) : IBarakoModule
    {
        public string Name { get; } = name;
        public IApplicationBuilder? Handed { get; private set; }

        public void ConfigureApp(IApplicationBuilder app)
        {
            Handed = app;
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Append(Header, Name);
                await next(context);
            });
        }
    }

    private sealed class Throwing(string name) : IBarakoModule
    {
        public string Name { get; } = name;

        public void ConfigureApp(IApplicationBuilder app) =>
            throw new NotSupportedException("the hook failed");
    }

    private sealed class Unbuildable(string name) : IBarakoModule
    {
        public string Name { get; } = name;

        public void ConfigureApp(IApplicationBuilder app) => app.Use(Fail);

        private static RequestDelegate Fail(RequestDelegate next) =>
            throw new NotSupportedException("the middleware failed");
    }

    private sealed class Hijacking(string name) : IBarakoModule
    {
        public string Name { get; } = name;

        public void ConfigureApp(IApplicationBuilder app) =>
            app.ApplicationServices = new ServiceCollection().BuildServiceProvider();
    }

    private static ApplicationBuilder NewApp() => new(new ServiceCollection().BuildServiceProvider());

    /// <summary>Ends the pipeline with a handler that records it was reached, and builds it.</summary>
    private static RequestDelegate Build(IApplicationBuilder app)
    {
        app.Run(context =>
        {
            context.Items[Reached] = true;
            return Task.CompletedTask;
        });
        return app.Build();
    }

    private static async Task<DefaultHttpContext> SendAsync(RequestDelegate pipeline, string path = "/api/anything")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await pipeline(context);
        return context;
    }

    [Fact]
    public async Task Module_middleware_runs_in_module_order_with_the_first_module_outermost()
    {
        var app = NewApp();

        ModuleAppPipeline.Use(app, [new Stamping("First"), new Stamping("Second")]);
        var context = await SendAsync(Build(app));

        var order = context.Response.Headers[Header].ToArray();
        order.Should().HaveCount(2);
        order.Should().Equal("First", "Second");
        context.Items.ContainsKey(Reached).Should().BeTrue("module middleware that calls next reaches what core added after it");
    }

    [Fact]
    public async Task A_module_with_the_default_hook_adds_nothing_to_the_pipeline()
    {
        var app = NewApp();

        ModuleAppPipeline.Use(app, [new Plain("A"), new Stamping("Stamps"), new Plain("B")]);
        var context = await SendAsync(Build(app));

        var order = context.Response.Headers[Header].ToArray();
        order.Should().HaveCount(1);
        order.Should().Equal("Stamps");
        context.Items.ContainsKey(Reached).Should().BeTrue();
    }

    [Fact]
    public async Task Modules_that_all_keep_the_default_hook_leave_the_request_untouched()
    {
        var app = NewApp();

        ModuleAppPipeline.Use(app, [new Plain("A"), new Plain("B")]);
        var context = await SendAsync(Build(app));

        context.Items.ContainsKey(Reached).Should().BeTrue();
        context.Response.Headers.ContainsKey(Header).Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_hook_that_throws_names_its_module_and_leaves_no_module_middleware_behind()
    {
        var app = NewApp();
        var use = () => ModuleAppPipeline.Use(app, [new Stamping("Earlier"), new Throwing("Broken")]);

        use.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Broken'*ConfigureApp*")
            .WithInnerException<NotSupportedException>();

        // The module ahead of the one that threw had already run its hook. Nothing it added may
        // be in the pipeline, or a host that carried on would serve a half-configured one.
        var context = await SendAsync(Build(app));
        context.Items.ContainsKey(Reached).Should().BeTrue();
        context.Response.Headers.ContainsKey(Header).Should().BeFalse();
    }

    [Fact]
    public void Middleware_that_cannot_be_built_fails_the_build_naming_its_module()
    {
        var app = NewApp();
        ModuleAppPipeline.Use(app, [new Stamping("Fine"), new Unbuildable("Broken")]);

        var build = () => Build(app);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Broken'*")
            .WithInnerException<NotSupportedException>();
    }

    [Fact]
    public async Task The_health_probes_go_around_module_middleware()
    {
        var app = NewApp();
        ModuleAppPipeline.Use(app, [new Stamping("Stamps")]);
        var pipeline = Build(app);

        var api = await SendAsync(pipeline, "/api/anything");
        var ready = await SendAsync(pipeline, "/health/ready");
        var root = await SendAsync(pipeline, "/health");

        api.Response.Headers.ContainsKey(Header).Should().BeTrue("the control: the same pipeline stamps any other path");
        ready.Response.Headers.ContainsKey(Header).Should().BeFalse();
        ready.Items.ContainsKey(Reached).Should().BeTrue();
        root.Response.Headers.ContainsKey(Header).Should().BeFalse();
        root.Items.ContainsKey(Reached).Should().BeTrue();
    }

    /// <summary>
    /// A host that calls <c>UseRouting</c> itself leaves its route builder in the builder
    /// properties, which a branch would otherwise inherit and a module could map endpoints onto.
    /// </summary>
    [Fact]
    public async Task A_branch_does_not_carry_the_route_builder_of_a_host_that_called_UseRouting()
    {
        await using var host = WebApplication.CreateBuilder().Build();
        host.UseRouting();
        IApplicationBuilder app = host;
        var stamping = new Stamping("Stamps");

        app.Properties.ContainsKey(ModuleAppPipeline.EndpointRouteBuilderKey).Should().BeTrue(
            "the control: UseRouting left the host's route builder where a branch would copy it from");

        ModuleAppPipeline.Use(app, [stamping]);

        stamping.Handed.Should().NotBeNull();
        stamping.Handed!.Properties.ContainsKey(ModuleAppPipeline.EndpointRouteBuilderKey).Should().BeFalse();
        stamping.Handed.Properties.ContainsKey(ModuleAppPipeline.GlobalEndpointRouteBuilderKey).Should().BeFalse();
        app.Properties.ContainsKey(ModuleAppPipeline.EndpointRouteBuilderKey).Should().BeTrue(
            "removing it from the branch leaves the host's own in place");
    }

    [Fact]
    public void A_module_is_handed_a_branch_that_shares_the_container_and_cannot_replace_it()
    {
        var app = NewApp();
        var services = app.ApplicationServices;
        var stamping = new Stamping("Stamps");

        ModuleAppPipeline.Use(app, [stamping, new Hijacking("Hijacks")]);

        stamping.Handed.Should().NotBeNull();
        stamping.Handed.Should().NotBeSameAs(app, "a module never holds the host's own builder");
        stamping.Handed!.ApplicationServices.Should().BeSameAs(services);
        app.ApplicationServices.Should().BeSameAs(services,
            "a module replacing the container on its branch must not change it for core's middleware");
    }
}
