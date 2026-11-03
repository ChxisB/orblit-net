namespace Orblit.Net.Game;

/// <summary>
/// Marks a method of the game that clients call on the server. Call <see cref="NetCalls.Bind"/> from
/// <c>Start</c> and the server runs the method when a client calls it by name.
/// </summary>
/// <remarks>
/// The method returns void. Each parameter is a bool, an int, a float, a string, a
/// <see cref="Orblit.VectorMath.Vector3"/> or a <see cref="NetEntity"/>, and the first may be a
/// <see cref="NetCaller"/>, which says which client called. The arguments come from a client, so
/// check them as you would any input.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerRpcAttribute : Attribute;

/// <summary>
/// Marks a method of the game that the server calls on a client, with
/// <see cref="NetCalls.CallClient"/> or <see cref="NetCalls.CallAll"/>. The rules for the method are
/// those of <see cref="ServerRpcAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClientRpcAttribute : Attribute;
