# TODO

- Smart discovery mode that combines both strategies: start from `rgOwnedApps` (catches unplayed F2P) but filter out DLC, soundtracks, demos, music apps, dedicated servers etc. so the user gets the wider coverage of `OnlyProfileGames: false` without the rotation pool getting flooded with non-game AppIDs. Probably needs a per-AppID `appdetails` lookup with a cached result, or a heuristic on the app type field. Make it the default once it works.
