using System.Collections.Generic;
using System.Text.Json;

namespace ASF.AutoIdle;

/// <summary>
/// Per-bot configuration for the AutoIdle plugin.
/// Lives inside a bot's JSON config file under the "AutoIdle" key.
/// All properties are optional; defaults are shown below.
///
/// Parsed manually instead of via JsonSerializer.Deserialize because ASF
/// is published with aggressive trimming that strips the reflection paths
/// System.Text.Json relies on for object construction. Manual parsing
/// uses only EnumerateObject + plain string comparisons, which always survive.
/// </summary>
public sealed class PluginConfig {
	public const string ConfigKey = "AutoIdle";

	public bool Enabled { get; set; } = true;
	public byte MaxGamesAtOnce { get; set; } = 32;
	public uint RotationMinutes { get; set; } = 60;
	public bool ExcludeFreeToPlay { get; set; } // deprecated, no-op
	public bool OnlyProfileGames { get; set; } = true; // default: use IPlayerService.GetOwnedGames (matches profile "Games X" count). Set false to use store dynamicstore (returns every AppID including DLC)
	public HashSet<uint> Blacklist { get; set; } = [];
	public HashSet<uint> Whitelist { get; set; } = [];
	public bool PauseCardFarming { get; set; } = true;
	public uint InitialDelaySeconds { get; set; } = 30;

	internal static PluginConfig FromAdditionalProperties(IReadOnlyDictionary<string, JsonElement>? additional) {
		// No AutoIdle block in the bot config → opt-out for that bot.
		// To enable the plugin for a bot you must add at least
		//   "AutoIdle": { "Enabled": true }
		// to its JSON config.
		if (additional is null || !additional.TryGetValue(ConfigKey, out JsonElement element)) {
			return new PluginConfig { Enabled = false };
		}

		PluginConfig config = new();

		if (element.ValueKind != JsonValueKind.Object) {
			return config;
		}

		foreach (JsonProperty prop in element.EnumerateObject()) {
			switch (prop.Name) {
				case "Enabled":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.Enabled = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.Enabled = false; }
					break;
				case "MaxGamesAtOnce":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetByte(out byte max) && max > 0) { config.MaxGamesAtOnce = max; }
					break;
				case "RotationMinutes":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint mins) && mins > 0) { config.RotationMinutes = mins; }
					break;
				case "ExcludeFreeToPlay":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.ExcludeFreeToPlay = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.ExcludeFreeToPlay = false; }
					break;
				case "OnlyProfileGames":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.OnlyProfileGames = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.OnlyProfileGames = false; }
					break;
				case "Blacklist":
					config.Blacklist = ParseUintArray(prop.Value);
					break;
				case "Whitelist":
					config.Whitelist = ParseUintArray(prop.Value);
					break;
				case "PauseCardFarming":
					if (prop.Value.ValueKind == JsonValueKind.True) { config.PauseCardFarming = true; } else if (prop.Value.ValueKind == JsonValueKind.False) { config.PauseCardFarming = false; }
					break;
				case "InitialDelaySeconds":
					if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint delay)) { config.InitialDelaySeconds = delay; }
					break;
			}
		}

		return config;
	}

	private static HashSet<uint> ParseUintArray(JsonElement element) {
		HashSet<uint> result = [];
		if (element.ValueKind == JsonValueKind.Array) {
			foreach (JsonElement el in element.EnumerateArray()) {
				if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint val) && val > 0) {
					result.Add(val);
				}
			}
		}
		return result;
	}
}
