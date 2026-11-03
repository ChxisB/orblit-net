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
- `net.Call(name, args)` runs the handler named `name` on the server.
  `net.CallClient(clientId, name, args)` and `net.CallAll(name, args)` run
  it on one client or on every client. `net.On(name, handler)` holds a
  handler. A client cannot call a client.
- An argument is a bool, an int, a float, a string, a `Vector3` or a
  `NetEntity`, and a call takes at most 16. Any other type throws where the
  call is made.
- A call travels with the scene's messages, so the host's inbound budget
  applies to it. It runs on the game's thread at the start of the next
  update, in the order it was made.
- `[ServerRpc]` and `[ClientRpc]` mark methods of the game. `net.Bind(game)`
  holds one handler for each method of the role the net has, and checks
  the signature of every marked method on every end. A bad one throws
  `ArgumentException` naming the method.
- A call that cannot be read, names an entity the scene lacks or does not fit
  its method is dropped. `NetworkedServer.Malformed` and
  `NetworkedClient.Malformed` count it. A call for a name nothing handles is
  counted in `UnhandledCalls`.
- An offline game runs its own calls at once, `CallClient` and `CallAll`
  included, against the handlers held on its own net. Each game host makes
  its own offline net, so two games share no handlers.
- A call to a client goes out at the end of the update that made it, after
  the tick, in the order the calls were made. A client makes an entity when
  a tick carries it, so a call that names a new entity arrives after it.
- A handler that throws on a call from the other end does not stop the game.
  The call is counted in `FaultedCalls` and the exception is kept in
  `LastCallFault`. A call the game makes to itself throws to its caller.
- A move of an owned entity with a NaN or an infinity in it is dropped.
  The scene keeps the last good place.
- `NetworkedServer.Assign` throws `ArgumentException` for a client the
  server does not know.
- `INetGame` is the interface a game implements to name its variables. The
  editor reads `NetVars` before it builds the server and each client, so
  every end is made from one schema. A game with none runs with no
  variables.
