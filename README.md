# ASF-AutoIdle

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that **auto-detects every game on your Steam account and idles them in rotating batches of 32** (Steam's per-account cap), cycling through your whole library so each game accumulates roughly equal playtime over time.

No Steam Web API key. No game IDs to enter. Drop the DLL in `plugins/`, set `"AutoIdle": { "Enabled": true }` in your bot config, restart ASF.

## Features

- **Plug-and-play library discovery** - uses the bot's authenticated `IPlayerService.GetOwnedGames` (default) or the wider `dynamicstore` endpoint (every AppID the account has access to, including unplayed F2P).
- **Round-robin batch picking** - strict cycle ordering, not random. Every game in the eligible pool is played at least once before any game is re-played. Persisted queue position survives restarts.
- **Pool-sweep tracking** - `idleshow` shows `X/N played` progress through the current sweep, plus an ETA for completion based on batch size and rotation interval. `Pool sweeps completed (all-time)` counter.
- **Coexist with ASF's card farmer** - `AllowCardFarming` defaults to `true`. When ASF is actively farming a card, AutoIdle yields the play slot. When farming clears, AutoIdle silently resumes the same batch. Set false if you want AutoIdle to permanently own the slot (the original `PauseCardFarming=true` behaviour).
- **Heartbeat re-assert every 30 s** - Steam occasionally drops or overrides a `Play(batch)` call (e.g. after a disconnect/reconnect storm, after ASF's card farmer kicks in/out, after FreePackages claims something). AutoIdle silently re-asserts the current batch every 30 s so the worst-case "looks idling but actually playing nothing" window is bounded at 30 s.
- **Cross-plugin coordination** - listens for `idlepause` / `idleresume` from siblings (e.g. ASF-AutoAchievement) so they can take exclusive control of the play slot during their own work. The timer pauses too - the original batch resumes with the same time remaining instead of restarting the 60-min interval.
- **2-hour pause failsafe** - if a sibling plugin crashes mid-pause without sending `idleresume`, AutoIdle auto-clears the pause after 2 h so the bot never sits idle forever.
- **Whitelist + blacklist** - both via JSON config and via runtime commands. Whitelist games always go in the batch (reducing dynamic capacity); blacklist games are never played.
- **User game detection** - if you launch a Steam game on your PC, the bot loses its play slot. AutoIdle detects `IsPlayingPossible=false` and pauses idle. When you close the game, it re-asserts the current batch within 30 s.
- **Per-rotation pool refresh** - re-fetches your library on every rotation tick (~1 s API call). New games appear in the pool within one rotation interval; command-triggered restarts (`iadd`/`iblock`/etc.) skip the duplicate fetch via a 30 s back-to-back guard.
- **Per-game time tracking** - every batch's elapsed time is credited to each game. `idlestats` shows session + all-time totals, sorted by all-time desc. Persisted.
- **Pause attribution** - `idlestats` shows how much time was spent paused by each external source (e.g. AutoAchievement) for both the current session and all-time.
- **Plugin uptime tracking** - per-session and total across all sessions, persisted across restarts.
- **Live config reload** - edit a bot's JSON config and ASF re-runs `OnBotInitModules`; the plugin restarts that bot's rotation transparently when relevant fields change.

## Install

1. Download `ASF-AutoIdle.dll` from the [latest release](../../releases) (or build from source).
2. Drop it into `<your ASF folder>/plugins/ASF-AutoIdle/`.
3. Add to any bot config under `<ASF>/config/<BotName>.json`, anywhere inside the outer `{ ... }`:
   ```json
   "AutoIdle": { "Enabled": true }
   ```
4. Restart ASF.

You should see in the log:
```
ASF-AutoIdle vX.Y.Z.0 loaded - every bot's library will be idled in rotating batches of 32.
<Bot> > AutoIdle: rotation loop started.
<Bot> > AutoIdle: profile owned-games returned N entries.
<Bot> > AutoIdle: now idling 32 game(s).
  Pool sweep: 32/N played, K until pool repeats, ~Xh to finish sweep (started 0s ago)
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
    "AllowCardFarming": true,
    "Blacklist": [],
    "Whitelist": []
}
```

| Key | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | bool | `true` if block exists | Master switch for that bot. |
| `OnlyProfileGames` | bool | `true` | `true` = use `IPlayerService.GetOwnedGames` (~hundreds of profile games). `false` = use store `dynamicstore` endpoint (~thousands; includes DLC, demos, unplayed F2P). |
| `MaxGamesAtOnce` | byte | `32` | Steam's hard cap is 32. |
| `RotationMinutes` | uint | `60` | How often to rotate to the next batch. Min enforced: 5. |
| `InitialDelaySeconds` | uint | `30` | Wait after login before first rotation. Skipped on command-triggered restarts. |
| `AllowCardFarming` | bool | `true` | When `true`, AutoIdle yields the play slot whenever ASF's card farmer is active (so cards still drop). When `false`, AutoIdle permanently pauses card farming for this bot and keeps the slot. |
| `Blacklist` | uint[] | `[]` | AppIDs never idled. Always wins over whitelist. |
| `Whitelist` | uint[] | `[]` | AppIDs **always** in every batch. Each takes one of the `MaxGamesAtOnce` slots, reducing dynamic capacity. |

**Backward compat**: the old `PauseCardFarming` config key is still parsed and gets inverted into `AllowCardFarming` (`PauseCardFarming=true` → `AllowCardFarming=false`). Existing configs keep their previous behaviour without edits.

### Opt-out

A bot config without an `AutoIdle` block is ignored entirely. Or set `"AutoIdle": { "Enabled": false }` to log the opt-out explicitly.

## Runtime commands

Send these to a bot via ASF's command interface (web UI Commands tab, IPC, or a chat DM to the bot). Operator-level access is required. Pass the bot name as the first argument, or omit it to default to ASF's chosen bot.

| Command | Aliases | What it does |
|---|---|---|
| `idleshow [bot]` | `ishow`, `idlestatus` | Status: pool size, batch capacity, pool sweep progress + ETA, sweeps completed, whitelist, blacklist, current batch, external pause / card-farming status. |
| `idleadd [bot] <appid\|name>` | `iadd` | Add a game to the always-include whitelist. |
| `idleremove [bot] <appid\|name>` | `irm`, `iremove` | Remove from whitelist. |
| `idleblacklist [bot] <appid\|name>` | `iblock`, `ibl` | Add to never-play blacklist. |
| `idleblacklistremove [bot] <appid\|name>` | `iunblock`, `iblrm` | Remove from blacklist. |
| `idlerotation [bot] <minutes>` | `irot`, `iint`, `idleinterval` | Change rotation interval (0 to clear override; min 5). |
| `idlestats [bot] [N\|all]` | `istats`, `istat` | Per-game time tracking + total session/all-time + pause attribution. |
| `idletoggle [bot]` | `itoggle` | Toggle `OnlyProfileGames` (profile games vs dynamicstore). |
| `idlecards [bot]` | `icards` | Toggle `AllowCardFarming` (yield play slot to ASF card farmer vs own it permanently). |
| `idlepause [bot] [source]` | - | Sibling-plugin signal. Pauses the rotation. Source defaults to "an external plugin". |
| `idleresume [bot]` | - | Sibling-plugin signal. Clears the pause and resumes the previously-active batch with the remaining rotation timer carried over. |
| `idlehelp` | `ihelp` | Print the command list. |

State changes (`idleadd`, `idleblacklist`, `idlerotation`, `idletoggle`, `idlecards`) restart the rotation immediately so changes take effect within seconds.

### Persistence

Runtime state is saved per-bot in ASF's `BotDatabase` under the key `ASF.AutoIdle.State`. Survives ASF restarts. Includes:
- Persistent whitelist / blacklist additions
- `OnlyProfileGames` runtime override
- `AllowCardFarming` runtime override
- `RotationMinutes` runtime override
- All-time per-game playtime stats
- Rotation queue position (round-robin cycle survives restarts)
- Pool sweep progress + completed-sweeps counter + current sweep start time
- All-time external-pause durations per source (e.g. ASF-AutoAchievement)
- Plugin uptime baseline

JSON-config `Whitelist`/`Blacklist` are read on every `OnBotInitModules` and **merged** with the runtime persistent sets.

## Sibling-plugin coordination

When [ASF-AutoAchievement](https://github.com/sturq/ASF-AutoAchievement) is installed and scans your library, it sends an `idlepause ASF-AutoAchievement` command to all bots. AutoIdle's handler:

1. Snapshots the current batch and remaining rotation time.
2. Drops the play state so AutoAchievement can take the slot for its scan.
3. Logs `paused by ASF-AutoAchievement` (plus `(ASF card farmer is also active...)` if applicable).

When AutoAchievement finishes, it sends `idleresume`. AutoIdle's handler:

1. Restarts the rotation loop with `_resumeBatchOnNextRotation` set.
2. New loop iteration picks up the saved batch, shifts `sleepUntil` and `_lastRotationAt` forward by the pause duration - so the rotation timer is effectively paused and the same batch finishes its full 60-minute window.

The 2-hour pause failsafe activates if `idleresume` never arrives (e.g. AutoAchievement crashed mid-scan), so the bot can't end up stuck silent indefinitely.

## Build from source

Requires the .NET SDK matching your ArchiSteamFarm runtime TFM (currently `net10.0` for ASF 6.3.x).

You also need `ArchiSteamFarm.dll` and `SteamKit2.dll` from the [exact ASF release](https://github.com/JustArchiNET/ArchiSteamFarm/releases) you intend to load the plugin into. Place them in a folder, then point `ASF_DIR` at it (or use the project's default sibling `ASF/` folder).

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

- **Whitelist math** - every whitelist entry takes one of the 32 batch slots, reducing dynamic capacity. With whitelist=4 and pool=570, a full sweep takes `ceil(566/28) = 21` batches. `idleshow` makes this auditable via the `Batch capacity:` line.
- **Small libraries** - when `pool_size ≤ MaxGamesAtOnce + 1`, the round-robin can't avoid repeats within each batch (e.g. 33 games / 32 batch means 31 games carry over each rotation). The cycle still distributes playtime evenly across all games.
- **"Account is limited" warning** - Steam restriction. Limited bots can't drop cards. The plugin still idles games but trading cards won't appear.
- **`OnlineStatus`** - separate ASF setting. The plugin doesn't touch it. Playtime accrues regardless of online status.
- **Free-to-play games (default mode)** - `IPlayerService.GetOwnedGames` only returns F2P titles you've launched at least once. Set `OnlyProfileGames: false` to include all F2P your account has access to (~thousands of AppIDs, will include DLC/demos).

## Troubleshooting

### Plugin doesn't idle everything on my account / only a subset of my library shows up

If you want **every product on the account** to enter the rotation (not just the ones already on your public profile), the default discovery mode is too narrow. `OnlyProfileGames: true` uses Steam's `IPlayerService.GetOwnedGames`, which omits a bunch of stuff your account actually owns - unplayed F2P titles (Warzone, Apex Legends, PUBG, etc.), playtests, region-restricted apps, recently-claimed FreePackages additions before the profile catches up, and anything Steam decides not to expose through the profile-games endpoint. Same symptom in all those cases: library has N apps, plugin only sees a fraction.

Fix - add `"OnlyProfileGames": false` to the bot's `AutoIdle` block:

```json
"AutoIdle": {
    "Enabled": true,
    "OnlyProfileGames": false
}
```

Then restart the bot, or send `!idletoggle <botname>` in the ASF chat to flip it at runtime.

Side effect: DLC, soundtracks and demos enter the rotation pool too. Filter them out with `"Blacklist": [appid1, appid2, ...]` if you don't want them taking batch slots.

## License

AGPL-3.0 - see [LICENSE](LICENSE). Forks and hosted modifications must keep the source open.
