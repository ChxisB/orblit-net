# Changelog

## 0.1.0

- `NetworkedServer` and `NetworkedClient` are an `IGameNet` for each end of
  a networked game. Give one to `GameHost.UseNet` before the game starts.
- An entity with a `net` component is shared. The server gives it a network
  id and spawns it on every client, now and for each client that joins
  later. A removed entity is removed on every client. An entity with no
  `net` component never crosses the wire.
- Where an entity is travels as a replicated column, so it is delta
  compressed and, with an interpolation delay, blended. The `net`
  component's `sync` list says whether the transform travels.
- An entity with `server` authority is moved by the server's game and
  nobody else. A client write to one is refused and counted.
- An entity with `owner` authority is moved by its owner, once the server
  calls `Assign(entityId, clientId)`. The owner's scene is the truth for it,
  and the server's state is ignored on that client. Before an owner is
  named, the server moves it. `RemoveClient` hands what the client owned
  back to the server. A server move of an entity its owner holds is undone
  by the owner's place on the next update.
- A client that already holds an entity under the spawned id adopts it
  rather than making a second one.
- A server message a client cannot read is dropped and counted in
  `NetworkedClient.Malformed`.
- `NetVarSchema` declares the variables of a game with a type for each:
  bool, int, float or `Vector3`. Build the server and every client from
  the same schema. At most 63 names, because the place takes one of the 64
  columns a set holds.
- `net.Var<T>(entityId, name)` returns a handle. The server sets it and a
  client reads it, the way an entity's place is read. A name is shared by
  an entity when its `net` component lists it in `sync`. The owner of an
  entity with `owner` authority sets the variables it shares, and the
  server and the other clients read them.
- A handle on an offline net keeps its own value.
- `NetworkedClient` is built from `NetworkedClientOptions` and no longer
  from four arguments.
- A game that declares different variables on the two ends is not
  detected. The wire carries no schema.
