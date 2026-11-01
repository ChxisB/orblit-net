using Orblit.Core;
using Orblit.Net;

namespace Orblit.Net.Tests;

/// <summary>One side of a session: a world plus the components it replicates.</summary>
internal sealed class Peer : IDisposable
{
    public Peer(bool reverseRegistrationOrder, bool includeOwner = false, IEnumerable<string>? ownerWritable = null)
    {
        // Registering in opposite orders on the two peers proves the wire does not depend on local component ids.
        if (reverseRegistrationOrder)
        {
            Health = World.RegisterComponent("Health", ComponentKind.Int32);
            Velocity = World.RegisterComponent("Velocity", ComponentKind.Float32, 3);
            Position = World.RegisterComponent("Position", ComponentKind.Float32, 3);
            NetworkId = World.RegisterComponent("NetworkId", ComponentKind.Int64);
        }
        else
        {
            NetworkId = World.RegisterComponent("NetworkId", ComponentKind.Int64);
            Position = World.RegisterComponent("Position", ComponentKind.Float32, 3);
            Velocity = World.RegisterComponent("Velocity", ComponentKind.Float32, 3);
            Health = World.RegisterComponent("Health", ComponentKind.Int32);
        }

        Owner = World.RegisterComponent("Owner", ComponentKind.UInt32);
        Set = new ReplicationSet(
            includeOwner ? [Position, Velocity, Health, Owner] : [Position, Velocity, Health],
            ownerWritable);
    }

    public World World { get; } = new();

    public ComponentType NetworkId { get; }

    public ComponentType Position { get; }

    public ComponentType Velocity { get; }

    public ComponentType Health { get; }

    public ComponentType Owner { get; }

    public ReplicationSet Set { get; }

    public void Dispose() => World.Dispose();
}
