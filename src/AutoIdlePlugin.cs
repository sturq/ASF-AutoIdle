using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;

namespace ASF.AutoIdle;

/// <summary>
/// Auto-idle plugin. Each bot maintains a rotation pool (auto-discovered) and
/// a "always include" whitelist. Each rotation, every whitelist entry plus
/// random picks from the pool fill up to MaxGamesAtOnce slots.
///
/// Implements:
///   - IPlugin           : required, advertises plugin name/version
///   - IBotModules       : reads the per-bot "AutoIdle" config block
///   - IBotConnection    : starts/stops the rotation loop on Steam connect/disconnect
///   - IBot              : tears down state when a bot is destroyed
///   - IBotCommand2      : handles !idleshow / !idleadd / !idleremove / !idlepool / !idletoggle
/// </summary>
[Export(typeof(IPlugin))]
public sealed class AutoIdlePlugin : IPlugin, IBotModules, IBotConnection, IBot, IBotCommand2 {
	private static readonly ConcurrentDictionary<string, BotRuntime> Runtimes = new();

	public string Name => "ASF-AutoIdle";

	public Version Version => typeof(AutoIdlePlugin).Assembly.GetName().Version
		?? new Version(1, 0, 0, 0);

	public Task OnLoaded() {
		ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo(
			$"{Name} v{Version} loaded — every bot's library will be idled in rotating batches of 32. See !idlehelp for commands."
		);
		return Task.CompletedTask;
	}

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		PluginConfig config = PluginConfig.FromAdditionalProperties(additionalConfigProperties);
		BotRuntime runtime = Runtimes.GetOrAdd(bot.BotName, _ => new BotRuntime(bot));
		runtime.UpdateConfig(config);

		return Task.CompletedTask;
	}

	public async Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryGetValue(bot.BotName, out BotRuntime? runtime)) {
			await runtime.StopAsync().ConfigureAwait(false);
			runtime.Start();
		}
	}

	public async Task OnBotDisconnected(Bot bot, SteamKit2.EResult reason) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryGetValue(bot.BotName, out BotRuntime? runtime)) {
			await runtime.StopAsync().ConfigureAwait(false);
		}
	}

	public Task OnBotInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);
		return Task.CompletedTask;
	}

	public async Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Runtimes.TryRemove(bot.BotName, out BotRuntime? runtime)) {
			await runtime.DisposeAsync().ConfigureAwait(false);
		}
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0) {
			return null;
		}

		string cmd = args[0].ToUpperInvariant();

		// ASF dispatches the command to one bot (alphabetically first,
		// usually) and passes the entire remainder as args. If the user
		// wrote `<command> <botname> [args]`, redirect to that bot's
		// runtime; otherwise stay on the bot ASF picked for us.
		BotRuntime? runtime = null;
		string[] tail;
		if (args.Length > 1 && TryFindRuntime(args[1], out runtime)) {
			tail = args.Skip(2).ToArray();
		} else {
			Runtimes.TryGetValue(bot.BotName, out runtime);
			tail = args.Skip(1).ToArray();
		}

		if (runtime is null) {
			return null;
		}

		return cmd switch {
			"IDLESHOW" or "ISHOW" or "IDLESTATUS" => runtime.HandleShow(),
			"IDLEADD" or "IADD" => await runtime.HandleAddWhitelist(tail).ConfigureAwait(false),
			"IDLEREMOVE" or "IRM" or "IREMOVE" => runtime.HandleRemoveWhitelist(tail),
			"IDLEBLACKLIST" or "IBL" or "IBLOCK" => await runtime.HandleAddBlacklist(tail).ConfigureAwait(false),
			"IDLEBLACKLISTREMOVE" or "IBLRM" or "IUNBLOCK" => runtime.HandleRemoveBlacklist(tail),
			"IDLEROTATION" or "IROTATION" or "IROT" or "IDLEINTERVAL" or "IINT" => runtime.HandleRotation(tail),
			"IDLESTATS" or "ISTATS" or "ISTAT" => runtime.HandleStats(tail),
			"IDLETOGGLE" or "ITOGGLE" => runtime.HandleToggle(),
			"IDLEHELP" or "IHELP" => HelpText(),
			_ => null
		};
	}

	private static bool TryFindRuntime(string botName, out BotRuntime? runtime) {
		// Case-insensitive lookup so users don't have to match casing exactly.
		foreach (KeyValuePair<string, BotRuntime> kvp in Runtimes) {
			if (string.Equals(kvp.Key, botName, StringComparison.OrdinalIgnoreCase)) {
				runtime = kvp.Value;
				return true;
			}
		}
		runtime = null;
		return false;
	}

	private static string HelpText() => string.Join('\n', new[] {
		"AutoIdle commands:",
		"  idleshow [bot]                     — show current rotation + whitelist + blacklist",
		"  idleadd [bot] <appid|name>         — add a game to the always-include whitelist",
		"  idleremove [bot] <appid|name>      — remove a game from the whitelist",
		"  idleblacklist [bot] <appid|name>   — add a game to the never-play blacklist",
		"  idleblacklistremove [bot] <appid|name> — remove a game from the blacklist",
		"  idlerotation [bot] <minutes>       — change rotation interval (0 to clear override; min 5)",
		"  idlestats [bot] [N|all]            — show top N tracked games (all-time + this session)",
		"  idletoggle [bot]                   — toggle OnlyProfileGames (profile games vs all owned)",
		"  idlehelp                           — this message",
	});
}

/// <summary>
/// Per-bot mutable state: current config, the rotation task, its cancellation
/// source, persistent whitelist and pool additions, and a name cache.
/// </summary>
internal sealed class BotRuntime : IAsyncDisposable {
	private const string PersistKey = "ASF.AutoIdle.State";

	private readonly Bot _bot;
	private readonly Random _rng = new();
	private readonly object _gate = new();
	private readonly Dictionary<uint, string> _nameCache = new();
	private readonly HashSet<uint> _persistentWhitelist = [];
	private readonly HashSet<uint> _persistentBlacklist = [];

	private PluginConfig _config = new();
	private CancellationTokenSource? _cts;
	private Task? _loop;
	private List<uint> _currentPool = [];
	private List<uint> _currentWhitelistBatch = [];
	private List<uint> _currentDynamicBatch = [];
	private DateTime? _lastRotationAt;
	private uint _lastRotationIntervalMinutes;
	private bool? _onlyProfileGamesOverride;
	private uint? _rotationMinutesOverride;
	private bool _skipNextInitialDelay;
	private bool _persistentLoaded;

	// Per-game time tracking. Both keyed by AppID; values are seconds.
	// _allTimeSeconds is persisted in BotDatabase; _sessionSeconds is in-memory only.
	private readonly Dictionary<uint, long> _allTimeSeconds = new();
	private readonly Dictionary<uint, long> _sessionSeconds = new();
	private readonly DateTime _sessionStartedAt = DateTime.UtcNow;
	private List<uint> _accountingBatch = [];
	private DateTime? _accountingBatchStartedAt;

	// Cumulative seconds the plugin has been running across all prior
	// sessions, loaded from BotDatabase. Total uptime = baseline + (now - _sessionStartedAt).
	private long _totalUptimeBaselineSeconds;

	internal BotRuntime(Bot bot) {
		_bot = bot ?? throw new ArgumentNullException(nameof(bot));
	}

	// ------------------------------------------------------------------
	// Lifecycle
	// ------------------------------------------------------------------

	internal void UpdateConfig(PluginConfig config) {
		ArgumentNullException.ThrowIfNull(config);

		EnsurePersistentLoaded();

		bool restart;
		bool changed;
		lock (_gate) {
			changed = !ConfigEquivalent(_config, config);
			restart = changed && _loop is { IsCompleted: false };
			_config = config;
		}

		if (changed) {
			_bot.ArchiLogger.LogGenericInfo(
				$"AutoIdle config: Enabled={config.Enabled}, OnlyProfileGames={EffectiveOnlyProfileGames(config)}, "
				+ $"MaxGamesAtOnce={config.MaxGamesAtOnce}, RotationMinutes={config.RotationMinutes}, "
				+ $"InitialDelaySeconds={config.InitialDelaySeconds}, PauseCardFarming={config.PauseCardFarming}, "
				+ $"ConfigBlacklist={config.Blacklist.Count}, ConfigWhitelist={config.Whitelist.Count}, "
				+ $"PersistentBlacklist={_persistentBlacklist.Count}, PersistentWhitelist={_persistentWhitelist.Count}"
			);
		}

		if (restart) {
			_bot.ArchiLogger.LogGenericInfo("AutoIdle: config changed, restarting rotation.");
			_ = Task.Run(async () => {
				await StopAsync().ConfigureAwait(false);
				Start();
			});
		}
	}

	internal void Start() {
		PluginConfig cfg;
		lock (_gate) {
			if (_loop is { IsCompleted: false }) {
				return;
			}
			cfg = _config;
			_cts = new CancellationTokenSource();
		}

		if (!cfg.Enabled) {
			_bot.ArchiLogger.LogGenericInfo("AutoIdle: disabled in config for this bot.");
			return;
		}

		CancellationToken token = _cts!.Token;
		_loop = Task.Run(() => RotateAsync(token));
		_bot.ArchiLogger.LogGenericInfo("AutoIdle: rotation loop started.");
	}

	internal void Stop() {
		CancellationTokenSource? cts;
		lock (_gate) { cts = _cts; }
		if (cts is not null) {
			try { cts.Cancel(); } catch (ObjectDisposedException) { }
		}
	}

	internal async Task StopAsync() {
		Task? loop;
		CancellationTokenSource? cts;
		lock (_gate) {
			loop = _loop;
			cts = _cts;
		}

		if (cts is not null) {
			try { cts.Cancel(); } catch (ObjectDisposedException) { }
		}

		if (loop is not null) {
			try { await loop.ConfigureAwait(false); } catch { }
		}

		lock (_gate) {
			if (ReferenceEquals(_cts, cts)) { _cts = null; }
			if (ReferenceEquals(_loop, loop)) { _loop = null; }
		}
		cts?.Dispose();
	}

	public async ValueTask DisposeAsync() {
		await StopAsync().ConfigureAwait(false);
	}

	private void RestartImmediately() {
		lock (_gate) { _skipNextInitialDelay = true; }
		_ = Task.Run(async () => {
			await StopAsync().ConfigureAwait(false);
			Start();
		});
	}

	// ------------------------------------------------------------------
	// Rotation loop
	// ------------------------------------------------------------------

	private async Task RotateAsync(CancellationToken token) {
		try {
			PluginConfig cfg;
			bool skipDelay;
			lock (_gate) {
				cfg = _config;
				skipDelay = _skipNextInitialDelay;
				_skipNextInitialDelay = false;
			}

			if (!skipDelay && cfg.InitialDelaySeconds > 0) {
				await Task.Delay(TimeSpan.FromSeconds(cfg.InitialDelaySeconds), token).ConfigureAwait(false);
			}

			if (cfg.PauseCardFarming) {
				try {
					await _bot.Actions.Pause(true).ConfigureAwait(false);
				} catch (Exception ex) {
					_bot.ArchiLogger.LogGenericException(ex);
				}
			}

			List<uint> pool = await DiscoverPoolAsync(cfg).ConfigureAwait(false);
			lock (_gate) { _currentPool = pool; }

			HashSet<uint> effectiveWhitelist = EffectiveWhitelist(cfg);
			int eligibleCount = pool.Count + effectiveWhitelist.Count;

			if (eligibleCount == 0) {
				_bot.ArchiLogger.LogGenericWarning("AutoIdle: no eligible games found, idling will not start.");
				return;
			}

			_bot.ArchiLogger.LogGenericInfo(
				$"AutoIdle: discovered {pool.Count} pool game(s), whitelist={effectiveWhitelist.Count}; rotating up to {cfg.MaxGamesAtOnce} every {EffectiveRotationMinutes(cfg)} min."
			);

			DateTime nextRefresh = DateTime.UtcNow.AddHours(12);

			while (!token.IsCancellationRequested) {
				lock (_gate) { cfg = _config; }

				if (DateTime.UtcNow >= nextRefresh) {
					List<uint> fresh = await DiscoverPoolAsync(cfg).ConfigureAwait(false);
					if (fresh.Count > 0) {
						pool = fresh;
						lock (_gate) { _currentPool = pool; }
					}
					nextRefresh = DateTime.UtcNow.AddHours(12);
				}

				(List<uint> whitelistBatch, List<uint> dynamicBatch) = PickBatch(pool, cfg);
				List<uint> batch = [.. whitelistBatch, .. dynamicBatch];
				lock (_gate) {
					_currentWhitelistBatch = whitelistBatch;
					_currentDynamicBatch = dynamicBatch;
				}

				uint minutes = EffectiveRotationMinutes(cfg);

				try {
					(bool ok, string msg) = await _bot.Actions.Play(batch).ConfigureAwait(false);
					if (ok) {
						RecordPreviousBatchTime();
						lock (_gate) {
							_lastRotationAt = DateTime.UtcNow;
							_lastRotationIntervalMinutes = minutes;
							_accountingBatch = [.. batch];
							_accountingBatchStartedAt = DateTime.UtcNow;
						}
						LogBatch(whitelistBatch, dynamicBatch);
						SavePersistentStateLocked();
					} else {
						_bot.ArchiLogger.LogGenericWarning($"AutoIdle: Bot.Actions.Play failed — {msg}");
					}
				} catch (Exception ex) {
					_bot.ArchiLogger.LogGenericException(ex);
				}

				try {
					await Task.Delay(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					break;
				}
			}
		} catch (OperationCanceledException) {
			// expected on stop
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		} finally {
			// Close out the trailing batch's tracked time before we stop.
			try { RecordPreviousBatchTime(); SavePersistentStateLocked(); } catch { }
			try { _bot.Actions.Resume(); } catch { }
			_bot.ArchiLogger.LogGenericInfo("AutoIdle: rotation loop stopped.");
		}
	}

	private void RecordPreviousBatchTime() {
		List<uint> previousBatch;
		DateTime? previousStart;
		lock (_gate) {
			previousBatch = _accountingBatch;
			previousStart = _accountingBatchStartedAt;
			_accountingBatch = [];
			_accountingBatchStartedAt = null;
		}

		if (!previousStart.HasValue || previousBatch.Count == 0) {
			return;
		}

		long seconds = (long) (DateTime.UtcNow - previousStart.Value).TotalSeconds;
		if (seconds <= 0) {
			return;
		}

		lock (_gate) {
			foreach (uint id in previousBatch) {
				_allTimeSeconds.TryGetValue(id, out long all);
				_allTimeSeconds[id] = all + seconds;
				_sessionSeconds.TryGetValue(id, out long sess);
				_sessionSeconds[id] = sess + seconds;
			}
		}
	}

	private void SavePersistentStateLocked() {
		lock (_gate) { SavePersistentState(); }
	}

	private void LogBatch(List<uint> whitelistBatch, List<uint> dynamicBatch) {
		int total = whitelistBatch.Count + dynamicBatch.Count;
		_bot.ArchiLogger.LogGenericInfo($"AutoIdle: now idling {total} game(s).");
		if (whitelistBatch.Count > 0) {
			_bot.ArchiLogger.LogGenericInfo($"  Whitelisted ({whitelistBatch.Count}): {FormatList(whitelistBatch)}");
		}
		if (dynamicBatch.Count > 0) {
			_bot.ArchiLogger.LogGenericInfo($"  Dynamic ({dynamicBatch.Count}): {FormatList(dynamicBatch)}");
		}
	}

	// ------------------------------------------------------------------
	// Discovery + batching
	// ------------------------------------------------------------------

	private async Task<List<uint>> DiscoverPoolAsync(PluginConfig cfg) {
		if (EffectiveOnlyProfileGames(cfg)) {
			IReadOnlyDictionary<uint, string>? owned = await GameDiscovery.GetProfileGamesAsync(_bot).ConfigureAwait(false);
			if (owned is null) {
				return [];
			}
			lock (_gate) {
				foreach (KeyValuePair<uint, string> kvp in owned) {
					if (!string.IsNullOrEmpty(kvp.Value)) {
						_nameCache[kvp.Key] = kvp.Value;
					}
				}
			}
			return [.. owned.Keys];
		}

		IReadOnlyCollection<uint> ids = await GameDiscovery.GetOwnedAppIDsAsync(_bot).ConfigureAwait(false);
		return [.. ids];
	}

	private (List<uint> whitelistBatch, List<uint> dynamicBatch) PickBatch(List<uint> pool, PluginConfig cfg) {
		HashSet<uint> blacklist = EffectiveBlacklist(cfg);
		HashSet<uint> whitelist = EffectiveWhitelist(cfg);

		// Blacklist always wins.
		whitelist.ExceptWith(blacklist);

		byte max = cfg.MaxGamesAtOnce == 0 ? (byte) 1 : cfg.MaxGamesAtOnce;
		List<uint> whitelistBatch = whitelist.Take(max).ToList();
		int remaining = max - whitelistBatch.Count;

		if (remaining <= 0) {
			return (whitelistBatch, []);
		}

		// Build the dynamic candidate set.
		HashSet<uint> dynamicCandidates = [.. pool];
		dynamicCandidates.ExceptWith(whitelistBatch);
		dynamicCandidates.ExceptWith(blacklist);

		if (dynamicCandidates.Count == 0) {
			return (whitelistBatch, []);
		}

		List<uint> dynamicList = dynamicCandidates.ToList();
		int take = Math.Min(remaining, dynamicList.Count);

		// Fisher–Yates partial shuffle.
		for (int i = 0; i < take; i++) {
			int j = _rng.Next(i, dynamicList.Count);
			(dynamicList[i], dynamicList[j]) = (dynamicList[j], dynamicList[i]);
		}

		return (whitelistBatch, dynamicList.Take(take).ToList());
	}

	private bool EffectiveOnlyProfileGames(PluginConfig cfg) {
		lock (_gate) {
			return _onlyProfileGamesOverride ?? cfg.OnlyProfileGames;
		}
	}

	private uint EffectiveRotationMinutes(PluginConfig cfg) {
		uint? overrideValue;
		lock (_gate) { overrideValue = _rotationMinutesOverride; }
		uint chosen = overrideValue ?? cfg.RotationMinutes;
		return Math.Max(5u, chosen);
	}

	private HashSet<uint> EffectiveWhitelist(PluginConfig cfg) {
		HashSet<uint> result = [.. cfg.Whitelist];
		lock (_gate) {
			result.UnionWith(_persistentWhitelist);
		}
		return result;
	}

	private HashSet<uint> EffectiveBlacklist(PluginConfig cfg) {
		HashSet<uint> result = [.. cfg.Blacklist];
		lock (_gate) {
			result.UnionWith(_persistentBlacklist);
		}
		return result;
	}

	// ------------------------------------------------------------------
	// Display helpers
	// ------------------------------------------------------------------

	private string FormatID(uint appID) {
		string name;
		lock (_gate) {
			_nameCache.TryGetValue(appID, out name!);
		}
		return string.IsNullOrEmpty(name) ? $"AppID {appID}" : $"{name} ({appID})";
	}

	private string FormatList(IEnumerable<uint> ids) =>
		string.Join(", ", ids.Select(FormatID));

	// Returns the per-game tracked time including the in-flight batch's
	// elapsed-but-not-yet-recorded time, so users see fresh numbers
	// immediately rather than only after each rotation tick commits.
	private (Dictionary<uint, long> allTime, Dictionary<uint, long> sessionTime, DateTime sessionStart) SnapshotStats() {
		Dictionary<uint, long> allTime;
		Dictionary<uint, long> sessionTime;
		DateTime sessionStart;
		List<uint> liveBatch;
		DateTime? liveBatchStart;

		lock (_gate) {
			allTime = new Dictionary<uint, long>(_allTimeSeconds);
			sessionTime = new Dictionary<uint, long>(_sessionSeconds);
			sessionStart = _sessionStartedAt;
			liveBatch = [.. _accountingBatch];
			liveBatchStart = _accountingBatchStartedAt;
		}

		if (liveBatchStart.HasValue && liveBatch.Count > 0) {
			long liveSecs = (long) (DateTime.UtcNow - liveBatchStart.Value).TotalSeconds;
			if (liveSecs > 0) {
				foreach (uint id in liveBatch) {
					allTime.TryGetValue(id, out long all);
					allTime[id] = all + liveSecs;
					sessionTime.TryGetValue(id, out long sess);
					sessionTime[id] = sess + liveSecs;
				}
			}
		}

		return (allTime, sessionTime, sessionStart);
	}

	private static string FormatDuration(TimeSpan d) {
		if (d < TimeSpan.Zero) {
			d = TimeSpan.Zero;
		}
		if (d.TotalDays >= 1) {
			return ((int) d.TotalDays).ToString(CultureInfo.InvariantCulture) + "d "
				+ d.Hours.ToString(CultureInfo.InvariantCulture) + "h";
		}
		if (d.TotalHours >= 1) {
			return ((int) d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h "
				+ d.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
		}
		if (d.TotalMinutes >= 1) {
			return ((int) d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m "
				+ d.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
		}
		return ((int) d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
	}

	// ------------------------------------------------------------------
	// Commands
	// ------------------------------------------------------------------

	internal string HandleShow() {
		List<uint> whitelistBatch;
		List<uint> dynamicBatch;
		List<uint> pool;
		PluginConfig cfg;
		bool? overrideOpg;
		DateTime? lastRotation;
		uint lastInterval;

		lock (_gate) {
			whitelistBatch = [.. _currentWhitelistBatch];
			dynamicBatch = [.. _currentDynamicBatch];
			pool = [.. _currentPool];
			cfg = _config;
			overrideOpg = _onlyProfileGamesOverride;
			lastRotation = _lastRotationAt;
			lastInterval = _lastRotationIntervalMinutes;
		}
		HashSet<uint> effectiveWhitelist = EffectiveWhitelist(cfg);
		HashSet<uint> effectiveBlacklist = EffectiveBlacklist(cfg);

		// Each interpolated string is materialized into a plain string before
		// being added to the list, which keeps us off StringBuilder.AppendLine's
		// AppendInterpolatedStringHandler overload (trimmed by ASF).
		List<string> lines = [];
		lines.Add($"AutoIdle status for {_bot.BotName}:");
		lines.Add($"  Enabled: {cfg.Enabled}");
		lines.Add($"  OnlyProfileGames: {EffectiveOnlyProfileGames(cfg)}{(overrideOpg.HasValue ? " (runtime override)" : "")}");
		lines.Add($"  Pool size: {pool.Count}");

		uint effectiveInterval = EffectiveRotationMinutes(cfg);
		if (lastRotation.HasValue) {
			TimeSpan elapsed = DateTime.UtcNow - lastRotation.Value;
			TimeSpan interval = TimeSpan.FromMinutes(lastInterval);
			TimeSpan remaining = interval - elapsed;
			if (remaining < TimeSpan.Zero) { remaining = TimeSpan.Zero; }
			lines.Add($"  Rotation: every {effectiveInterval} min, last batch {FormatDuration(elapsed)} ago, next batch in {FormatDuration(remaining)}");
		} else {
			lines.Add($"  Rotation: every {effectiveInterval} min (waiting for first batch)");
		}

		lines.Add($"  Whitelist: {effectiveWhitelist.Count} game(s)");
		if (effectiveWhitelist.Count > 0) {
			lines.Add($"    {FormatList(effectiveWhitelist)}");
		}
		lines.Add($"  Blacklist: {effectiveBlacklist.Count} game(s)");
		if (effectiveBlacklist.Count > 0) {
			lines.Add($"    {FormatList(effectiveBlacklist)}");
		}
		lines.Add($"  Currently idling: {whitelistBatch.Count + dynamicBatch.Count} game(s)");
		if (whitelistBatch.Count > 0) {
			lines.Add($"    Whitelisted ({whitelistBatch.Count}): {FormatList(whitelistBatch)}");
		}
		if (dynamicBatch.Count > 0) {
			lines.Add($"    Dynamic ({dynamicBatch.Count}): {FormatList(dynamicBatch)}");
		}
		if (whitelistBatch.Count == 0 && dynamicBatch.Count == 0) {
			lines.Add("    (nothing yet — rotation hasn't picked the first batch)");
		}

		return string.Join('\n', lines);
	}

	internal Task<string?> HandleAddWhitelist(string[] args) => HandleAddTo(args, _persistentWhitelist, "whitelist", "idleadd");
	internal Task<string?> HandleAddBlacklist(string[] args) => HandleAddTo(args, _persistentBlacklist, "blacklist", "idleblacklist");
	internal string HandleRemoveWhitelist(string[] args) => HandleRemoveFrom(args, _persistentWhitelist, "whitelist", "idleremove");
	internal string HandleRemoveBlacklist(string[] args) => HandleRemoveFrom(args, _persistentBlacklist, "blacklist", "idleblacklistremove");

	private async Task<string?> HandleAddTo(string[] args, HashSet<uint> set, string label, string usageCmd) {
		if (args.Length == 0) {
			return $"Usage: !{usageCmd} <appid|name>";
		}

		string target = string.Join(' ', args).Trim().Trim('"');
		uint? appID = await ResolveAppIDAsync(target).ConfigureAwait(false);

		if (!appID.HasValue) {
			return $"Couldn't find a game matching '{target}' in this bot's library. Try the AppID number instead.";
		}

		bool added;
		lock (_gate) {
			added = set.Add(appID.Value);
			if (added) {
				SavePersistentState();
			}
		}

		string formatted = FormatID(appID.Value);

		if (!added) {
			return $"{formatted} is already in the {label}.";
		}

		RestartImmediately();
		return $"Added {formatted} to the {label}. Rotation restarting now.";
	}

	private string HandleRemoveFrom(string[] args, HashSet<uint> set, string label, string usageCmd) {
		if (args.Length == 0) {
			return $"Usage: !{usageCmd} <appid|name>";
		}

		string target = string.Join(' ', args).Trim().Trim('"');
		uint? appID = TryParseAppID(target) ?? FindByName(target);

		if (!appID.HasValue) {
			return $"Couldn't find '{target}'. Pass the AppID number to remove an entry whose name isn't cached.";
		}

		bool removed;
		lock (_gate) {
			removed = set.Remove(appID.Value);
			if (removed) {
				SavePersistentState();
			}
		}

		string formatted = FormatID(appID.Value);
		if (!removed) {
			return $"{formatted} wasn't in the runtime {label} (config-defined {label} entries must be removed in the JSON config).";
		}

		RestartImmediately();
		return $"Removed {formatted} from the {label}. Rotation restarting now.";
	}

	internal string HandleStats(string[] args) {
		// idlestats [N|all] — every game by all-time desc by default.
		int top = int.MaxValue;
		if (args.Length > 0) {
			string raw = args[0].Trim();
			if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase)) {
				top = int.MaxValue;
			} else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) {
				top = n;
			} else {
				return "Usage: !idlestats [N|all]   — every tracked game (default), or top N.";
			}
		}

		(Dictionary<uint, long> allTime, Dictionary<uint, long> sessionTime, DateTime sessionStart) = SnapshotStats();

		if (allTime.Count == 0 && sessionTime.Count == 0) {
			return $"AutoIdle stats for {_bot.BotName}: no rotation tracked yet.";
		}

		long totalAll = 0;
		foreach (long v in allTime.Values) { totalAll += v; }
		long totalSession = 0;
		foreach (long v in sessionTime.Values) { totalSession += v; }

		List<KeyValuePair<uint, long>> ordered = allTime.OrderByDescending(static kvp => kvp.Value).ToList();
		int shown = Math.Min(top, ordered.Count);

		TimeSpan sessionUptime = DateTime.UtcNow - sessionStart;
		long totalUptimeSecs;
		lock (_gate) {
			totalUptimeSecs = _totalUptimeBaselineSeconds + (long) sessionUptime.TotalSeconds;
		}

		List<string> lines = [];
		lines.Add($"AutoIdle stats for {_bot.BotName}:");
		lines.Add(shown == ordered.Count
			? $"  Tracked games: {allTime.Count} (all listed below)"
			: $"  Tracked games: {allTime.Count} (top {shown} below)");
		lines.Add($"  Plugin uptime (this session): {FormatDuration(sessionUptime)}");
		lines.Add($"  Plugin uptime (all sessions): {FormatDuration(TimeSpan.FromSeconds(totalUptimeSecs))}");
		lines.Add($"  Total tracked (all-time, summed): {FormatDuration(TimeSpan.FromSeconds(totalAll))}");
		lines.Add($"  Total tracked (this session, summed): {FormatDuration(TimeSpan.FromSeconds(totalSession))}");
		lines.Add($"  Session started: {sessionStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
		lines.Add("");

		for (int i = 0; i < shown; i++) {
			KeyValuePair<uint, long> kvp = ordered[i];
			sessionTime.TryGetValue(kvp.Key, out long sessionForGame);
			// Pad manually — interpolated alignment ({x,3}) compiles to a
			// trimmed-away AppendFormatted overload in ASF's runtime.
			string idx = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3);
			lines.Add($"  {idx}. {FormatID(kvp.Key)}");
			lines.Add($"        all-time {FormatDuration(TimeSpan.FromSeconds(kvp.Value))}, session {FormatDuration(TimeSpan.FromSeconds(sessionForGame))}");
		}

		return string.Join('\n', lines);
	}

	internal string HandleRotation(string[] args) {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		if (args.Length == 0) {
			return $"Rotation interval: {EffectiveRotationMinutes(cfg)} min\nUsage: !idlerotation <minutes>   (0 to reset to default)";
		}

		string raw = args[0].Trim();
		if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint minutes)) {
			return $"'{raw}' is not a number. Pass a positive integer of minutes (or 0 to reset).";
		}

		if (minutes == 0) {
			lock (_gate) {
				_rotationMinutesOverride = null;
				SavePersistentState();
			}
			RestartImmediately();
			return $"Reset. Rotation interval is now {cfg.RotationMinutes} min.";
		}

		if (minutes < 5) {
			return "Minimum rotation interval is 5 minutes (Steam rate-limits faster cycling). Pass a value >= 5.";
		}

		lock (_gate) {
			_rotationMinutesOverride = minutes;
			SavePersistentState();
		}
		RestartImmediately();
		return $"Rotation interval is now {minutes} min. Rotation restarting now.";
	}

	internal string HandleToggle() {
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		bool current = EffectiveOnlyProfileGames(cfg);
		bool newValue = !current;

		lock (_gate) {
			_onlyProfileGamesOverride = newValue;
			SavePersistentState();
		}

		RestartImmediately();
		string source = newValue
			? "IPlayerService.GetOwnedGames (~hundreds of profile games)"
			: "store dynamicstore (~thousands, includes DLC etc.)";
		return $"OnlyProfileGames is now {newValue} (runtime override). Next discovery uses {source}.";
	}

	private async Task<uint?> ResolveAppIDAsync(string input) {
		uint? parsed = TryParseAppID(input);
		if (parsed.HasValue) {
			return parsed;
		}

		uint? cached = FindByName(input);
		if (cached.HasValue) {
			return cached;
		}

		// Cache may not be populated yet — force a discovery and retry once.
		PluginConfig cfg;
		lock (_gate) { cfg = _config; }

		if (EffectiveOnlyProfileGames(cfg)) {
			IReadOnlyDictionary<uint, string>? owned = await GameDiscovery.GetProfileGamesAsync(_bot).ConfigureAwait(false);
			if (owned is not null) {
				lock (_gate) {
					foreach (KeyValuePair<uint, string> kvp in owned) {
						if (!string.IsNullOrEmpty(kvp.Value)) {
							_nameCache[kvp.Key] = kvp.Value;
						}
					}
				}
				return FindByName(input);
			}
		}

		return null;
	}

	private static uint? TryParseAppID(string input) =>
		uint.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) && id > 0 ? id : null;

	private uint? FindByName(string input) {
		lock (_gate) {
			// exact match first (case-insensitive)
			foreach (KeyValuePair<uint, string> kvp in _nameCache) {
				if (string.Equals(kvp.Value, input, StringComparison.OrdinalIgnoreCase)) {
					return kvp.Key;
				}
			}
			// substring fallback
			foreach (KeyValuePair<uint, string> kvp in _nameCache) {
				if (kvp.Value.Contains(input, StringComparison.OrdinalIgnoreCase)) {
					return kvp.Key;
				}
			}
		}
		return null;
	}

	// ------------------------------------------------------------------
	// Persistent state (whitelist + extra pool + override) via BotDatabase
	// ------------------------------------------------------------------

	private void EnsurePersistentLoaded() {
		lock (_gate) {
			if (_persistentLoaded) {
				return;
			}
			_persistentLoaded = true;
		}

		try {
			JsonElement state = _bot.BotDatabase.LoadFromJsonStorage(PersistKey);
			if (state.ValueKind != JsonValueKind.Object) {
				return;
			}

			lock (_gate) {
				if (TryGetProp(state, "whitelist", out JsonElement wl) && wl.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement el in wl.EnumerateArray()) {
						if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint id) && id > 0) {
							_persistentWhitelist.Add(id);
						}
					}
				}
				if (TryGetProp(state, "blacklist", out JsonElement bl) && bl.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement el in bl.EnumerateArray()) {
						if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint id) && id > 0) {
							_persistentBlacklist.Add(id);
						}
					}
				}
				if (TryGetProp(state, "onlyProfileGamesOverride", out JsonElement opg)) {
					if (opg.ValueKind == JsonValueKind.True) { _onlyProfileGamesOverride = true; } else if (opg.ValueKind == JsonValueKind.False) { _onlyProfileGamesOverride = false; }
				}
				if (TryGetProp(state, "rotationMinutesOverride", out JsonElement rot)
					&& rot.ValueKind == JsonValueKind.Number
					&& rot.TryGetUInt32(out uint rotMin)
					&& rotMin >= 5) {
					_rotationMinutesOverride = rotMin;
				}
				if (TryGetProp(state, "allTimeStats", out JsonElement statsEl)
					&& statsEl.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in statsEl.EnumerateObject()) {
						if (uint.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint appID)
							&& appID > 0
							&& prop.Value.ValueKind == JsonValueKind.Number
							&& prop.Value.TryGetInt64(out long secs)
							&& secs >= 0) {
							_allTimeSeconds[appID] = secs;
						}
					}
				}
				if (TryGetProp(state, "totalUptimeSeconds", out JsonElement upt)
					&& upt.ValueKind == JsonValueKind.Number
					&& upt.TryGetInt64(out long uptSecs)
					&& uptSecs >= 0) {
					_totalUptimeBaselineSeconds = uptSecs;
				}
			}
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		}
	}

	private void SavePersistentState() {
		// Caller holds _gate. Build JSON via plain string concatenation to
		// stay clear of any StringBuilder / interpolation overloads that
		// might have been removed by ASF's assembly trimming.
		string whitelistCsv = string.Join(",", _persistentWhitelist.Select(static x => x.ToString(CultureInfo.InvariantCulture)));
		string blacklistCsv = string.Join(",", _persistentBlacklist.Select(static x => x.ToString(CultureInfo.InvariantCulture)));
		string overridePart = _onlyProfileGamesOverride.HasValue
			? (",\"onlyProfileGamesOverride\":" + (_onlyProfileGamesOverride.Value ? "true" : "false"))
			: "";
		string rotationPart = _rotationMinutesOverride.HasValue
			? (",\"rotationMinutesOverride\":" + _rotationMinutesOverride.Value.ToString(CultureInfo.InvariantCulture))
			: "";

		StringBuilder statsSb = new();
		statsSb.Append("{");
		bool firstStat = true;
		foreach (KeyValuePair<uint, long> kvp in _allTimeSeconds) {
			if (!firstStat) {
				statsSb.Append(",");
			}
			statsSb.Append("\"");
			statsSb.Append(kvp.Key.ToString(CultureInfo.InvariantCulture));
			statsSb.Append("\":");
			statsSb.Append(kvp.Value.ToString(CultureInfo.InvariantCulture));
			firstStat = false;
		}
		statsSb.Append("}");
		string statsPart = ",\"allTimeStats\":" + statsSb.ToString();

		long totalUptime = _totalUptimeBaselineSeconds + (long) (DateTime.UtcNow - _sessionStartedAt).TotalSeconds;
		string uptimePart = ",\"totalUptimeSeconds\":" + totalUptime.ToString(CultureInfo.InvariantCulture);

		string json = "{\"whitelist\":[" + whitelistCsv + "],\"blacklist\":[" + blacklistCsv + "]"
			+ overridePart + rotationPart + statsPart + uptimePart + "}";

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement clone = doc.RootElement.Clone();
			_bot.BotDatabase.SaveToJsonStorage(PersistKey, clone);
		} catch (Exception ex) {
			_bot.ArchiLogger.LogGenericException(ex);
		}
	}

	private static bool TryGetProp(in JsonElement element, string name, out JsonElement value) {
		if (element.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty prop in element.EnumerateObject()) {
				if (prop.Name == name) {
					value = prop.Value;
					return true;
				}
			}
		}
		value = default;
		return false;
	}

	// ------------------------------------------------------------------
	// Misc
	// ------------------------------------------------------------------

	private static bool ConfigEquivalent(PluginConfig a, PluginConfig b) =>
		a.Enabled == b.Enabled
		&& a.MaxGamesAtOnce == b.MaxGamesAtOnce
		&& a.RotationMinutes == b.RotationMinutes
		&& a.ExcludeFreeToPlay == b.ExcludeFreeToPlay
		&& a.OnlyProfileGames == b.OnlyProfileGames
		&& a.PauseCardFarming == b.PauseCardFarming
		&& a.InitialDelaySeconds == b.InitialDelaySeconds
		&& a.Blacklist.SetEquals(b.Blacklist)
		&& a.Whitelist.SetEquals(b.Whitelist);
}
