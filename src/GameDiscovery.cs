using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;

namespace ASF.AutoIdle;

/// <summary>
/// Discovers AppIDs to idle. Two strategies:
///   - GetOwnedAppIDsAsync   = every AppID the bot has access to via the
///                             store dynamicstore endpoint (~thousands;
///                             includes DLC, soundtracks, demos).
///   - GetProfileGamesAsync  = same data IPlayerService.GetOwnedGames returns;
///                             matches the public "Games X" count on the
///                             profile because that's the same call the
///                             profile UI uses.
/// </summary>
internal static class GameDiscovery {
	private const string StoreHost = "https://store.steampowered.com";

	internal static async Task<IReadOnlyCollection<uint>> GetOwnedAppIDsAsync(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (bot.SteamID == 0) {
			return [];
		}

		Uri uri = new($"{StoreHost}/dynamicstore/userdata/?id_required=0");

		ObjectResponse<JsonElement>? response;
		try {
			response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(uri).ConfigureAwait(false);
		} catch (Exception ex) {
			bot.ArchiLogger.LogGenericException(ex);
			return [];
		}

		if (response is null || response.Content.ValueKind != JsonValueKind.Object) {
			bot.ArchiLogger.LogGenericWarning("AutoIdle: failed to fetch owned-games userdata.");
			return [];
		}

		if (!TryGetProp(response.Content, "rgOwnedApps", out JsonElement ownedApps)
			|| ownedApps.ValueKind != JsonValueKind.Array) {
			bot.ArchiLogger.LogGenericWarning("AutoIdle: rgOwnedApps missing from userdata response.");
			return [];
		}

		HashSet<uint> appIDs = new(ownedApps.GetArrayLength());
		foreach (JsonElement el in ownedApps.EnumerateArray()) {
			if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint appID) && appID > 0) {
				appIDs.Add(appID);
			}
		}

		return appIDs;
	}

	/// <summary>
	/// Returns the same list IPlayerService.GetOwnedGames returns - i.e. the
	/// games that count toward the public "Games X" number on the profile.
	/// Uses ASF's wrapper for SteamKit's unified messaging, so it talks to
	/// Steam over the bot's already-authenticated protocol connection. No
	/// Web API key required.
	/// </summary>
	/// <summary>
	/// Returns the same list IPlayerService.GetOwnedGames returns - i.e. the
	/// games that count toward the public "Games X" number on the profile.
	/// Dictionary key is AppID, value is the game's display name.
	/// </summary>
	internal static async Task<IReadOnlyDictionary<uint, string>?> GetProfileGamesAsync(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (bot.SteamID == 0) {
			return null;
		}

		Dictionary<uint, string>? response;
		try {
			response = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);
		} catch (Exception ex) {
			bot.ArchiLogger.LogGenericException(ex);
			return null;
		}

		if (response is null) {
			bot.ArchiLogger.LogGenericWarning("AutoIdle: GetOwnedGames returned null (Steam refused, profile private, or query timed out).");
			return null;
		}

		bot.ArchiLogger.LogGenericInfo($"AutoIdle: profile owned-games returned {response.Count} entries.");
		return response;
	}

	// ASF is published with aggressive assembly trimming that strips
	// JsonElement.TryGetProperty(string, ...) and JsonProperty.NameEquals(string).
	// Manual iteration with plain string == survives trimming.
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
}
