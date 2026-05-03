# ASF-AutoIdle

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that **auto-detects every game on your Steam profile** and idles them in rotating batches of 32 (Steam's per-account simultaneous-game cap), reshuffling on a configurable interval so playtime accumulates evenly across the whole library.

No Steam Web API key. No game IDs to enter. Drop the DLL in `plugins/`, set `"AutoIdle": { "Enabled": true }` in your bot config, restart ASF.

## Features

- **Plug-and-play** — discovers owned games via the bot's authenticated Steam protocol session (`IPlayerService.GetOwnedGames`). Matches the public profile's "Games X" count: paid games + played free-to-play, no DLC / soundtracks / demos.
- **Per-bot opt-in** — bots without an `AutoIdle` block are completely ignored.
- **Whitelist & blacklist** — both via JSON config and via runtime commands. Persisted in `BotDatabase`.
- **Even distribution** — 60-min default rotation reshuffles which 32 of your N games are loaded each cycle, so over time every game accrues roughly equal hours.
- **Auto-refresh** — re-fetches your library every 12 hours, new purchases get picked up automatically.
- **Per-game time tracking** — every batch's elapsed time is credited to each game. All-time totals persist; session totals reset at startup.
- **Plugin uptime tracking** — both per-session and total across all sessions, persisted across restarts.
- **Live config reload** — edit a bot's JSON config and ASF re-runs `OnBotInitModules`; the plugin restarts that bot's rotation transparently.

## Install

1. Download `ASF-AutoIdle.dll` from the [latest release](../../releases) (or build from source — see below).
2. Drop it into `<your ASF folder>/plugins/ASF-AutoIdle/`.
3. Add to any bot config under `<ASF>/config/<BotName>.json`, anywhere inside the outer `{ ... }`:
   ```json
   "AutoIdle": { "Enabled": true }
   ```
4. Restart ASF.

You should see in the log:
```
ASF-AutoIdle vX.Y.Z.0 loaded — every bot's library will be idled in rotating batches of 32.
<Bot> > AutoIdle: rotation loop started.
<Bot> > AutoIdle: profile owned-games returned N entries.
<Bot> > AutoIdle: now idling 32 game(s).
```

## Configuration

Every key is optional. Defaults shown.

```json
"AutoIdle": {
    "Enabled": true,
    "OnlyProfileGames": true,
    "MaxGamesAtOnce": 32,
    "RotationMinutes": 60,
    "InitialDelaySeconds": 30,
    "PauseCardFarming": true,
    "Blacklist": [],
    "Whitelist": []
}
```

| Key | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | bool | `true` if block exists | Master switch for that bot. |
| `OnlyProfileGames` | bool | `true` | `true` = use `IPlayerService.GetOwnedGames` (matches profile "Games X" count). `false` = use store dynamicstore endpoint (returns every owned AppID including DLC). |
| `MaxGamesAtOnce` | byte | `32` | Steam's hard cap is 32. |
| `RotationMinutes` | uint | `60` | How often to reshuffle. Min enforced: 5. |
| `InitialDelaySeconds` | uint | `30` | Wait after login before first rotation. |
| `PauseCardFarming` | bool | `true` | Pause ASF's card-farmer so it doesn't fight the plugin. |
| `Blacklist` | uint[] | `[]` | AppIDs to never idle. Always wins over whitelist. |
| `Whitelist` | uint[] | `[]` | AppIDs that are **always** in the rotation. Each takes one of the `MaxGamesAtOnce` slots. |

### Opt-out

A bot config without an `AutoIdle` block is ignored entirely. Or set `"AutoIdle": { "Enabled": false }` to log the opt-out explicitly.

## Runtime commands

Send these to a bot via ASF's command interface (web UI Commands tab, IPC, or a chat DM to the bot). Operator-level access is required. Pass the bot name as the first argument, or omit it to default to ASF's chosen bot.

| Command | Aliases | What it does |
|---|---|---|
| `idleshow [bot]` | `ishow`, `idlestatus` | Show pool size, whitelist, blacklist, current rotation. |
| `idleadd [bot] <appid\|name>` | `iadd` | Add a game to the always-include whitelist. |
| `idleremove [bot] <appid\|name>` | `irm`, `iremove` | Remove from whitelist. |
| `idleblacklist [bot] <appid\|name>` | `iblock`, `ibl` | Add to never-play blacklist. |
| `idleblacklistremove [bot] <appid\|name>` | `iunblock`, `iblrm` | Remove from blacklist. |
| `idlerotation [bot] <minutes>` | `irot`, `iint` | Change rotation interval (0 to clear override; min 5). |
| `idlestats [bot] [N\|all]` | `istats` | Per-game time tracking. Default: every tracked game, sorted by all-time desc. |
| `idletoggle [bot]` | `itoggle` | Toggle `OnlyProfileGames` (profile games vs all owned). |
| `idlehelp` | `ihelp` | Print the command list. |

State changes (`idleadd`, `idleblacklist`, `idlerotation`, `idletoggle`) restart the rotation immediately so changes take effect within seconds.

### Persistence

Runtime overrides — whitelist additions, blacklist additions, `OnlyProfileGames` toggle, `RotationMinutes` override, all-time per-game stats, total uptime — are saved per-bot in ASF's `BotDatabase` under the key `ASF.AutoIdle.State`. They survive ASF restarts.

JSON-config `Whitelist`/`Blacklist` are read on every `OnBotInitModules` call and **merged** with the runtime persistent sets at runtime.

## Build from source

Requires the .NET SDK matching your ArchiSteamFarm runtime TFM (currently `net10.0` for ASF 6.3.x).

You also need a copy of `ArchiSteamFarm.dll` and `SteamKit2.dll` from the [exact ASF release](https://github.com/JustArchiNET/ArchiSteamFarm/releases) you intend to load the plugin into. Place them in a folder, then point `ASF_DIR` at it (or use the project's default sibling `ASF/` folder).

```bash
# Linux / macOS
ASF_DIR=/path/to/ArchiSteamFarm dotnet publish src/ASF-AutoIdle.csproj -c Release -o ./publish
```

```powershell
# Windows
$env:ASF_DIR = "C:\path\to\ArchiSteamFarm"
dotnet publish src\ASF-AutoIdle.csproj -c Release -o .\publish
```

The compiled `ASF-AutoIdle.dll` ends up in `./publish`. Copy it into `<ASF>/plugins/ASF-AutoIdle/`.

## Notes / gotchas

- **"Account is limited" warning** — Steam restriction. Bots that haven't spent money can't farm cards. The plugin will still idle games on them, but trading cards won't drop.
- **`OnlineStatus`** — separate ASF setting. The plugin doesn't touch it. If you set `"OnlineStatus": 0` (Offline), playtime still accrues but friends won't see the account as "In-game".
- **Free-to-play games** — `IPlayerService.GetOwnedGames` includes only F2P games you've actually launched. Once the plugin idles an F2P long enough that Steam considers it played, it'll appear on the profile.

## License

MIT — see [LICENSE](LICENSE).
