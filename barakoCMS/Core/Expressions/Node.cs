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

/// <summary>
/// A run of one precedence level, such as <c>a + b - c</c> or <c>a and b and c</c>, read left to
/// right. It is one node however long the run is, so the tree stays flat and the evaluator loops.
/// Operator <c>i</c> sits between operand <c>i</c> and operand <c>i + 1</c>.
/// </summary>
internal sealed class ChainNode(int position, IReadOnlyList<Node> operands, IReadOnlyList<string> ops, IReadOnlyList<int> opPositions)
    : Node(position, operands.Max(o => o.Height) + 1)
{
    public IReadOnlyList<Node> Operands { get; } = operands;

    public IReadOnlyList<string> Ops { get; } = ops;

    public IReadOnlyList<int> OpPositions { get; } = opPositions;
}

internal sealed class CallNode(int position, string name, IReadOnlyList<Node> args)
    : Node(position, (args.Count == 0 ? 0 : args.Max(a => a.Height)) + 1)
{
    public string Name { get; } = name;

    public IReadOnlyList<Node> Args { get; } = args;
}
