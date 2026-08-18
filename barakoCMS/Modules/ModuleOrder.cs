namespace barakoCMS.Modules;

/// <summary>
/// Orders modules so that every module runs after the ones it declares a dependency on.
/// </summary>
internal static class ModuleOrder
{
    /// <summary>
    /// Topologically sorts <paramref name="modules"/> by <see cref="IBarakoModule.DependsOn"/>.
    /// </summary>
    /// <remarks>
    /// Stable: modules with no dependency relationship keep the order they were given, so the same
    /// inputs always produce the same build. That matters more once discovery replaces a hand-written
    /// list, because assembly scan order is not something to rely on.
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

        var ordered = new List<IBarakoModule>(modules.Count);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 visiting, 2 done
        var path = new Stack<string>();

        void Visit(IBarakoModule module)
        {
            if (state.TryGetValue(module.Name, out var s))
            {
                if (s == 2) return;

                // Back edge: the path from here to the repeat is the cycle, printed so the reader
                // does not have to reconstruct it from a list of names.
                var cycle = path.Reverse().SkipWhile(n => !n.Equals(module.Name, StringComparison.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    "Module dependencies form a cycle: "
                    + string.Join(" -> ", cycle) + " -> " + module.Name
                    + ". One of these DependsOn declarations has to go.");
            }

            state[module.Name] = 1;
            path.Push(module.Name);

            foreach (var dependency in module.DependsOn)
            {
                if (!byName.TryGetValue(dependency, out var target))
                {
                    throw new InvalidOperationException(
                        $"Module '{module.Name}' depends on '{dependency}', which is not registered. "
                        + $"Register it, or remove it from {module.GetType().Name}.DependsOn. "
                        + $"Registered modules: {string.Join(", ", modules.Select(m => m.Name))}.");
                }
                Visit(target);
            }

            path.Pop();
            state[module.Name] = 2;
            ordered.Add(module);
        }

        // Iterated in the given order, so independent modules come out in the order they went in.
        foreach (var module in modules)
            Visit(module);

        return ordered;
    }
}
