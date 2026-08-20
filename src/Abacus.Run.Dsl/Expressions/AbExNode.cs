namespace Abacus.Run.Dsl.Expressions;

/// <summary>Which object a path is rooted in.</summary>
public enum AbExRoot
{
    /// <summary><c>$</c> — the current message's <c>data</c>.</summary>
    Data,

    /// <summary><c>$ctx</c> — the frozen start context, reachable from every node.</summary>
    Context,

    /// <summary><c>$run</c> — instance, tenant, attempt, superstep, workflow, version, now.</summary>
    Run
}

/// <summary>One step of a path: a property name or an array index.</summary>
public readonly record struct AbExSegment(string? Name, int Index)
{
    public bool IsIndex => Name is null;

    public static AbExSegment Property(string name) => new(name, -1);
    public static AbExSegment At(int index) => new(null, index);

    public override string ToString() => IsIndex ? $"[{Index}]" : $".{Name}";
}

/// <summary>
/// An immutable AbEx syntax tree. Parsed once at registration and evaluated many times, so nothing
/// here holds evaluation state.
/// </summary>
public abstract record AbExNode
{
    /// <summary>Nesting depth, used to enforce the configured limit at validation time.</summary>
    public abstract int Depth { get; }

    /// <summary>Source offset of the construct's first token, for diagnostics.</summary>
    public int Offset { get; init; }
}

public sealed record AbExLiteral(AbExValue Value) : AbExNode
{
    public override int Depth => 1;
}

public sealed record AbExPath(AbExRoot Root, IReadOnlyList<AbExSegment> Segments) : AbExNode
{
    public override int Depth => 1;

    /// <summary>True for <c>$run.now</c>, the one value that differs between two evaluations.</summary>
    public bool IsNonDeterministic =>
        Root == AbExRoot.Run && Segments.Count > 0 &&
        string.Equals(Segments[0].Name, "now", StringComparison.Ordinal);

    public override string ToString() =>
        (Root switch { AbExRoot.Data => "$", AbExRoot.Context => "$ctx", _ => "$run" }) +
        string.Concat(Segments);
}

public sealed record AbExUnary(string Operator, AbExNode Operand) : AbExNode
{
    public override int Depth => Operand.Depth + 1;
}

public sealed record AbExBinary(string Operator, AbExNode Left, AbExNode Right) : AbExNode
{
    public override int Depth => Math.Max(Left.Depth, Right.Depth) + 1;
}

public sealed record AbExCall(string Name, IReadOnlyList<AbExNode> Arguments) : AbExNode
{
    public override int Depth => Arguments.Count == 0 ? 1 : Arguments.Max(a => a.Depth) + 1;
}
