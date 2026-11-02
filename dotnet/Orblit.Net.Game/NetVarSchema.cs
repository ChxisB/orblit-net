using Orblit.Scene;

namespace Orblit.Net.Game;

/// <summary>
/// The variables a networked game shares, with the type of each. Both ends are built from the same
/// schema, because a variable travels as a column and a column needs its type before the first
/// message.
/// </summary>
/// <remarks>
/// A variable is a bool, an int, a float or a <c>Vector3</c>, which are the types a column holds
/// exactly. A name is declared once for the whole game. Which entity shares it is the entity's
/// <c>net</c> component, whose <c>sync</c> list names the variables that travel.
/// <para>
/// The order the names are declared in does not matter. The replication set orders columns by name,
/// so two ends that list the same variables differently still lay them out the same way.
/// </para>
/// </remarks>
public sealed class NetVarSchema
{
    /// <summary>The transform column takes one of the sixty-four a replication set holds.</summary>
    public const int MaxVariables = 63;

    private readonly IReadOnlyList<KeyValuePair<string, NetVarKind>> _declared;
    private readonly Dictionary<string, NetVarKind> _kinds;

    private NetVarSchema(IReadOnlyList<KeyValuePair<string, NetVarKind>> declared)
    {
        _declared = declared;
        _kinds = new Dictionary<string, NetVarKind>(declared);
    }

    public static NetVarSchema Empty { get; } = new([]);

    public IReadOnlyList<string> Names => [.. _declared.Select(static pair => pair.Key)];

    internal IReadOnlyList<KeyValuePair<string, NetVarKind>> Declared => _declared;

    /// <summary>
    /// A schema that also holds <paramref name="name"/>. Declaring the same name with the same type
    /// again returns this schema.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is blank or is <c>transform</c>, the type is not one a variable holds, the name is
    /// already declared with another type, or this would be the sixty-fourth variable.
    /// </exception>
    public NetVarSchema With<T>(string name)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var kind = NetVarCodec.KindOf<T>();
        if (name == NetComponent.TransformState)
        {
            throw new ArgumentException("\"transform\" is the entity's place, so it cannot also name a variable.", nameof(name));
        }

        if (_kinds.TryGetValue(name, out var declared))
        {
            return declared == kind
                ? this
                : throw new ArgumentException($"\"{name}\" is already a {declared} variable, so it cannot also be a {kind}.", nameof(name));
        }

        if (_declared.Count == MaxVariables)
        {
            throw new ArgumentException(
                $"A networked game holds at most {MaxVariables} variables. \"{name}\" would be number {MaxVariables + 1}.",
                nameof(name));
        }

        return new NetVarSchema([.. _declared, new KeyValuePair<string, NetVarKind>(name, kind)]);
    }

    internal NetVarKind? KindOf(string name) => _kinds.TryGetValue(name, out var kind) ? kind : null;

    /// <summary>The declared variables a tag lists, which are the ones its entity shares.</summary>
    internal string[] SharedBy(NetComponent tag) => [.. tag.Sync.Where(_kinds.ContainsKey)];
}
