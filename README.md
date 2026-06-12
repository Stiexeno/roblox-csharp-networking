# roblox-csharp-networking

`RemoteEvent`-as-static-delegate for [roblox-csharp](https://github.com/Stiexeno/roblox-csharp). Tag a `static Action` field with `[NetworkEvent(...)]`, `+=` to subscribe, `-=` to unsubscribe, `?.Invoke(...)` to fire — the transpiler wires both ends to a `RemoteEvent` you never declare.

## Install

```sh
roblox-csharp plugin add Stiexeno/roblox-csharp-networking
```

Lands at `plugins/Networking/`; runtime mounts at `ReplicatedStorage.Plugins.Networking`. Remotes materialize under `ReplicatedStorage.Remotes.Events.<FieldName>` (created server-side only; clients wait for replication).

**Requires:** roblox-csharp 0.1.0-alpha.52+ (declared via `minTranspilerVersion`), plus the RobloxApi plugin (for `Player`).

## Usage

```csharp
using Networking;

public static class Events
{
    [NetworkEvent(Scope.ClientToServer)]
    public static Action<Player, string> ChatSubmitted;

    [NetworkEvent(Scope.ServerToClient)]
    public static Action<Player, int> ScoreUpdate;
}

// Server
Events.ChatSubmitted += OnChat;                  // OnServerEvent, validated
Events.ChatSubmitted -= OnChat;                  // disconnects that handler
Events.ScoreUpdate?.Invoke(player, 42);          // FireClient(player, 42)
Events.ScoreUpdate?.Invoke(null, 0);             // FireAllClients(0)

// Client
Events.ScoreUpdate += (player, score) => Show(score); // OnClientEvent
Events.ChatSubmitted?.Invoke(localPlayer, "hello");   // FireServer (Player arg dropped)
```

Player-parameter rules (`Action<Player, ...>` first type arg):

- `ClientToServer` — server handlers receive the firing `Player` first (Roblox supplies it). The client's `Invoke` drops its `Player` argument before `FireServer`.
- `ServerToClient` — the first `Invoke` argument is the target. The target is checked **at runtime**: `nil` (literal or variable) broadcasts via `FireAllClients`, anything else is `FireClient`. Client handlers receive `Players.LocalPlayer` injected as the first arg.

## Server-side validation

`ClientToServer` handlers are wrapped at the wire: each argument is checked against the delegate's parameter types — `number` / `string` / `boolean` / `Instance` / `Player` (`IsA` check). On mismatch the event is dropped with a `warn` naming the event and offending argument. Tables and other complex types pass through unvalidated — deep-check those yourself.

## Unsubscribe semantics

`field -= handler` disconnects the connection stored for that handler's identity (method group or held delegate). Removing a never-added handler is a no-op, matching C#. A fresh lambda never matches (same as C#) — keep a reference if you intend to remove it. Re-`+=`ing the same handler replaces the existing connection rather than stacking a duplicate.

## API surface

| Type | Purpose |
|---|---|
| `[NetworkEvent(Scope)]` | Marks a static delegate field as a networked wire. |
| `Scope.ClientToServer` | `FireServer` on invoke, `OnServerEvent` on subscribe. |
| `Scope.ServerToClient` | targeted/broadcast fire on invoke, `OnClientEvent` on subscribe. |

## How it works

At compile time the extension discovers every `[NetworkEvent]` field and:

- Emits `_NetworkEventsBootstrap.server.luau` into the Rojo-resolved server partition, calling `Networking.RegisterRemote(name)` per field, so every `RemoteEvent` exists at server boot.
- Prepends `local _evt_<name> = Networking.GetRemote("<name>")` preludes to files that reference an event — qualified (`Events.X`) or unqualified (`using static`).
- Rewrites `+=` / `-=` to runtime `Connect` / `Disconnect` (connection-tracked, validated), and `?.Invoke(...)` / `.Invoke(...)` to the matching fire.
- Skips emit for files containing only `[NetworkEvent]` declarations and suppresses imports of the declaring classes and plugin stubs.

## Compile-time errors (RC0020)

- **Mixed containers.** A class declaring `[NetworkEvent]` fields may contain nothing else — the class is compiled away, so other members would be unreachable.
- **Wrong-side usage.** Invoking a `ClientToServer` event from server-routed code (or subscribing from the wrong side, in either direction) is rejected when the file's server/client context is resolvable. `shared` files are not checked — route networking code into `server`/`client`.

## Caveats

- **Serialization is raw `RemoteEvent` semantics.** Only plain data survives the boundary: tables lose metatables (class instances arrive as plain tables, no methods), mixed/non-sequential keys are unreliable, `nil` holes truncate argument lists. Send primitives and plain data tables.
- **No `RemoteFunction`** (request/response), no throttling, no ordering guarantees beyond raw `RemoteEvent`.

## License

[MIT](LICENSE).
