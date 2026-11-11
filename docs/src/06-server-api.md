# Server API reference

A server part is a .NET 8 class library that references the server's `Blizzless.dll` with `Private=false`, so the mod doesn't ship its own copy. At startup the server loads every `Mods\<id>\server\*.dll`, or the `server.assembly` from `mod.json`, into its own process.

The namespace is `DiIiS_NA.Mods`.

## IServerMod

```csharp
public sealed class MyMod : IServerMod
{
    public void Initialize(ServerModContext context) { ... }
}
```

The server creates every public `IServerMod` class with a parameterless constructor once, and calls `Initialize`. This happens **before** the database schema is built and before any server listens. Only register things there: hooks, routes, settings. Don't touch the database or game state.

## ServerModContext

| Member | Meaning |
|---|---|
| `Id`, `Name`, `Version` | from `mod.json` |
| `Directory` | the installed folder `Mods\<id>`; read your settings from here |
| `Logger` | a logger named after the mod: `Info`, `Warn`, `Error`, `Debug`. It writes to the server console and log files. |
| `Hooks` | game events, see below |
| `RegisterRestRoute(prefix, handler)` | HTTP routes on the server's REST port |

## Server hooks

Hooks let a mod watch and change what the game does. Subscribe in `Initialize`:

```csharp
context.Hooks.OnGoldDrop(e => e.Amount *= 2);
```

- Handlers run in mod load order, on game threads, after the server's own rules. They see, and may change, the final values.
- An exception in a handler is logged with the mod id, and the other handlers still run.
- Keep handlers short and never block.

| Hook | Event object | When |
|---|---|---|
| `OnGoldDrop(Action<GoldDropEvent>)` | `Source` (Actor), `Player`, `Amount` (get/set) | Gold is about to drop for a player: a kill, a chest… The amount already includes the level table × game gold modifier × `RateMoney`. An `Amount` of 0 or less drops nothing. |

More hooks are added as mods need them, always as new `On...` methods. Existing ones are not changed.

## Commands

Any class in a mod assembly that derives from `CommandGroup` and has `[CommandGroup]` becomes a chat and console command, exactly like the server's own:

```csharp
[CommandGroup("goldrate", "Shows or sets the gold multiplier.", Account.UserLevels.User)]
public class GoldRateCommands : CommandGroup
{
    [DefaultCommand]
    public string Show(string[] @params, BattleClient invokerClient) => "...";

    [Command("set", "goldrate set <x>", Account.UserLevels.GM)]
    public string Set(string[] @params, BattleClient invokerClient) { ... }
}
```

- In game you type `!goldrate set 5`.
- `invokerClient` is null when the command comes from the server console.
- `inGameOnly: true` restricts a command to players in a game; there, `invokerClient.InGameClient.Player` is the player.
- User levels are `User`, `GM` and `Admin`. On release builds, a player whose level is too low gets "Unknown command".

## REST routes

```csharp
context.RegisterRestRoute("/mymod/", req => ModRestReply.Json("{\"ok\":true}"));
```

- Requests whose path starts with the prefix go to your handler on the REST port (`[REST]` in the server config).
- `ModRestRequest` has `Method`, `Path`, `Route`, `Query` and `Body`.
- Reply with `ModRestReply.Json`, `.Text`, `.NotFound` or `new ModRestReply(status, body, contentType)`.
- Handlers run on the REST thread: answer quickly.

To authenticate a player, the client part sends the login token (`login_token()`) in a POST body. The server part resolves it to an account; the Auction House mod shows how.

## Database tables

FluentNHibernate `ClassMap<T>` classes in a mod assembly are picked up like the server's own. Their tables are created and updated automatically at startup. To read and write, use the server's `DBSessions` helpers (`SessionQueryWhere`, `SessionSave`, `SessionUpdate`...).

For a mod assembly that uses NHibernate, also reference `NHibernate.dll` and `FluentNHibernate.dll` from the server folder with `Private=false`.

## Game code

A server mod runs in the server process and can use anything public in `Blizzless.dll`, for example `ItemGenerator`, `Player.Inventory` and `GameAttributes`.

Hooks are the stable surface; direct use of the server's classes can break when the server changes. If you need an event that has no hook yet, ask for one rather than patching the server.
