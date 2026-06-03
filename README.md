# roblox-csharp-networking

`RemoteEvent`-as-static-delegate for [roblox-csharp](https://github.com/Stiexeno/roblox-csharp). Tag a `static Action` field with `[NetworkEvent(...)]`, then `+=` to subscribe and `?.Invoke(...)` to fire — the transpiler wires both ends to a `RemoteEvent` you never have to declare.

## Install

```sh
roblox-csharp plugin add Stiexeno/roblox-csharp-networking
```

Lands at `plugins/Networking/`; runtime mounts at `ReplicatedStorage.Plugins.Networking`.

## Quick start

```csharp
using Networking;

public static class Events
{
    [NetworkEvent(Scope.ClientToServer)]
    public static Action<Player, string> ChatSubmitted;

    [NetworkEvent(Scope.ServerToClient)]
    public static Action<string> BroadcastBanner;
}

public class Server
{
    public Server()
    {
        Events.ChatSubmitted += (player, message) =>
        {
            Logger.Log($"{player.Name} said {message}");
            Events.BroadcastBanner?.Invoke($"{player.Name}: {message}");
        };
    }
}

public class Client
{
    public Client(Player localPlayer)
    {
        Events.BroadcastBanner += msg => ShowBanner(msg);
        Events.ChatSubmitted?.Invoke(localPlayer, "hello");
    }

    private void ShowBanner(string msg) { /* ... */ }
}
```

The first parameter on `Action<Player, ...>` is interpreted as the player the wire targets — `FireClient(player, ...)`, or `FireAllClients(...)` if you pass `null`. For `ClientToServer` events the runtime always passes the firing `Player` first, and the C# side receives it as the first arg automatically.

## API surface

| Type | Purpose |
|---|---|
| `[NetworkEvent(Scope)]` | Marks a static delegate field as a networked wire. |
| `Scope.ClientToServer` | `FireServer` on invoke, `OnServerEvent` on subscribe. |
| `Scope.ServerToClient` | `FireClient(player)` / `FireAllClients(...)` on invoke, `OnClientEvent` on subscribe. |

## How it works

At compile time the extension walks the symbol table, finds every `[NetworkEvent]`-tagged field, and:

- Emits a single `_NetworkEventsBootstrap.server.luau` that calls `Networking.RegisterRemote(name)` for each discovered field. Runs before user code, guaranteeing the `RemoteEvent` exists.
- Per source file that references one of those fields, prepends a `local _evt_<name> = Networking.GetRemote("<name>")` prelude so the remote handle is in scope.
- Rewrites every `field += handler` into the matching `:Connect` call, and every `field?.Invoke(args)` (or `field.Invoke(args)`) into the matching `:FireServer` / `:FireClient` / `:FireAllClients` call.
- Suppresses imports for files that contain *only* `[NetworkEvent]` field declarations — those files exist purely as type signatures and have no runtime body to emit.

## What's not in v1

- **`RemoteFunction` (request/response).** Only `RemoteEvent` (fire-and-forget) for now.
- **Per-event throttling / rate limits.** Bring your own.
- **Replication budgets / ordering guarantees.** Same semantics as raw `RemoteEvent`.

## License

[MIT](LICENSE).
