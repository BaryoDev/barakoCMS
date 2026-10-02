using barakoCMS.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Modules;

/// <summary>
/// Calls <see cref="IBarakoModule.ConfigureApp"/> and places what each module added at the point of
/// the pipeline <c>UseBarakoCMS</c> calls this from.
/// </summary>
internal static class ModuleAppPipeline
{
    /// <summary>
    /// Runs every module's hook, then adds the result to <paramref name="app"/> as one middleware.
    /// </summary>
    /// <remarks>
    /// Each module gets <c>app.New()</c>, a builder of its own, rather than <paramref name="app"/>.
    /// The host's builder is a <c>WebApplication</c>: handed over as it is, a module could cast it
    /// and map endpoints, or replace <c>ApplicationServices</c> for the core middleware added after
    /// it. A branch shares the container, keeps its own copy of the builder properties, and has no
    /// route table.
    ///
    /// Every hook runs before anything is added to <paramref name="app"/>, so a hook that throws
    /// leaves no other module's middleware behind.
    ///
    /// The health probes go around all of it, as they go around the output cache: a module that
    /// throttles or answers from a cache must not be able to decide whether a pod is restarted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A module threw in its hook, or its middleware could not be built. The message names it.
    /// </exception>
    public static void Use(IApplicationBuilder app, IEnumerable<IBarakoModule> modules)
    {
        var branches = new List<(IBarakoModule Module, IApplicationBuilder Branch)>();

        foreach (var module in modules)
        {
            var branch = app.New();
            try
            {
                module.ConfigureApp(branch);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}' threw in ConfigureApp, so the request pipeline was not built. "
                    + "Fix the module, or leave it off BarakoCMS:Modules:Enabled.", ex);
            }

            branches.Add((module, branch));
        }

        if (branches.Count == 0)
            return;

        app.Use(next =>
        {
            // Built back to front, so the first module in the order is the outermost.
            var pipeline = next;
            for (var i = branches.Count - 1; i >= 0; i--)
            {
                var (module, branch) = branches[i];
                try
                {
                    branch.Run(pipeline);
                    pipeline = branch.Build();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Module '{module.Name}' added middleware in ConfigureApp that could not be built. "
                        + "Fix the module, or leave it off BarakoCMS:Modules:Enabled.", ex);
                }
            }

            var modulePipeline = pipeline;
            return context => HealthProbePaths.IsHealthPath(context.Request.Path.Value)
                ? next(context)
                : modulePipeline(context);
        });
    }
}
