namespace barakoCMS.Modules;

/// <summary>
/// Orders modules so that every module runs after the ones it declares a dependency on.
/// </summary>
internal static class ModuleOrder
{
    /// <summary>One module's place in the traversal: which dependency it is up to.</summary>
    private sealed class Frame(IBarakoModule module, string[] dependencies)
    {
        public IBarakoModule Module { get; } = module;
        public string[] Dependencies { get; } = dependencies;
        public int Next { get; set; }
    }

    /// <summary>
    /// Topologically sorts <paramref name="modules"/> by <see cref="IBarakoModule.DependsOn"/>.
    /// </summary>
    /// <remarks>
    /// Stable: modules with no dependency relationship keep the order they were given, so the same
    /// inputs always produce the same build. That matters more once discovery replaces a hand-written
    /// list, because assembly scan order is not something to rely on.
    ///
    /// The traversal keeps its own stack on the heap rather than recursing. Recursion bounded
    /// dependency depth by the call stack, which this method never checked, so a deep enough chain
    /// killed the process instead of reporting anything. Nothing here caps depth: the graph is the
    /// host's, and any number picked would refuse a legal one. Depth now costs heap, which runs out
    /// with an exception a host can catch and a message that says which module it was on.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A declared dependency is not registered, or the graph contains a cycle.
    /// </exception>
    public static IReadOnlyList<IBarakoModule> Sort(IReadOnlyList<IBarakoModule> modules)
    {
        var byName = new Dictionary<string, IBarakoModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            if (byName.TryGetValue(m.Name, out var existing) && !ReferenceEquals(existing, m))
            {
                throw new InvalidOperationException(
                    $"Two modules are both named '{m.Name}' ({existing.GetType().FullName} and "
                    + $"{m.GetType().FullName}). Module names identify them to each other, so they must be unique.");
            }
            byName[m.Name] = m;
        }

        const int Visiting = 1;
        const int Done = 2;

        var ordered = new List<IBarakoModule>(modules.Count);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The chain currently being walked, root first, so a back edge can print the cycle.
        var path = new List<string>();
        var stack = new List<Frame>();

        void Push(IBarakoModule module)
        {
            state[module.Name] = Visiting;
            path.Add(module.Name);
            stack.Add(new Frame(module, module.DependsOn?.ToArray() ?? Array.Empty<string>()));
        }

        // Iterated in the given order, so independent modules come out in the order they went in.
        foreach (var root in modules)
        {
            if (state.TryGetValue(root.Name, out var rootState) && rootState == Done)
                continue;

            Push(root);

            while (stack.Count > 0)
            {
                var frame = stack[^1];

                if (frame.Next >= frame.Dependencies.Length)
                {
                    stack.RemoveAt(stack.Count - 1);
                    path.RemoveAt(path.Count - 1);
                    state[frame.Module.Name] = Done;
                    ordered.Add(frame.Module);
                    continue;
                }

                var dependency = frame.Dependencies[frame.Next++];

                if (!byName.TryGetValue(dependency, out var target))
                {
                    throw new InvalidOperationException(
                        $"Module '{frame.Module.Name}' depends on '{dependency}', which is not registered. "
                        + $"Register it, or remove it from {frame.Module.GetType().Name}.DependsOn. "
                        + $"Registered modules: {string.Join(", ", modules.Select(m => m.Name))}.");
                }

                if (state.TryGetValue(target.Name, out var targetState))
                {
                    if (targetState == Done) continue;

                    // Back edge: the path from the repeat onwards is the cycle, printed so the
                    // reader does not have to reconstruct it from a list of names.
                    var cycle = path.SkipWhile(n => !n.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                    throw new InvalidOperationException(
                        "Module dependencies form a cycle: "
                        + string.Join(" -> ", cycle) + " -> " + target.Name
                        + ". One of these DependsOn declarations has to go.");
                }

                Push(target);
            }
        }

        return ordered;
    }
}
