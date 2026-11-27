namespace barakoCMS.Core.Expressions;

/// <summary>
/// The logic functions. <c>iif</c>, <c>switch</c> and <c>coalesce</c> are lazy and the evaluator runs
/// them, so a branch not taken is never evaluated. Blank means null, or text that is only white space.
/// </summary>
internal static class LogicFunctions
{
    public static void Register(Action<FunctionDef> add)
    {
        add(new("iif", 3, 3, [Arg.Any], ExprType.Null, null));
        add(new("switch", 3, -1, [Arg.Any], ExprType.Null, null));
        add(new("coalesce", 2, -1, [Arg.Any], ExprType.Null, null));
        add(new("isblank", 1, 1, [Arg.Any], ExprType.Bool, (_, _, a) => Values.IsBlank(a[0]), false));
    }
}
