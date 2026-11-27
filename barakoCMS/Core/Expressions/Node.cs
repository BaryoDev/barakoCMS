namespace barakoCMS.Core.Expressions;

/// <summary>A node of the parsed formula. <see cref="Height"/> is the longest path down to a leaf, counting this node.</summary>
internal abstract class Node(int position, int height)
{
    public int Position { get; } = position;

    public int Height { get; } = height;
}

internal sealed class Literal(int position, object? value, ExprType type) : Node(position, 1)
{
    public object? Value { get; } = value;

    public ExprType Type { get; } = type;
}

/// <summary>A field of the entry, or one of the fixed system names. The checker sets <see cref="Type"/>.</summary>
internal sealed class NameNode(int position, string name, bool isSystem) : Node(position, 1)
{
    public string Name { get; } = name;

    public bool IsSystem { get; } = isSystem;

    public ExprType Type { get; set; }
}

internal sealed class UnaryNode(int position, string op, Node operand) : Node(position, operand.Height + 1)
{
    public string Op { get; } = op;

    public Node Operand { get; } = operand;
}

internal sealed class BinaryNode(int position, string op, Node left, Node right)
    : Node(position, Math.Max(left.Height, right.Height) + 1)
{
    public string Op { get; } = op;

    public Node Left { get; } = left;

    public Node Right { get; } = right;
}

internal sealed class CallNode(int position, string name, IReadOnlyList<Node> args)
    : Node(position, (args.Count == 0 ? 0 : args.Max(a => a.Height)) + 1)
{
    public string Name { get; } = name;

    public IReadOnlyList<Node> Args { get; } = args;
}
