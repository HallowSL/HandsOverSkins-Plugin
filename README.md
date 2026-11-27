# HandsOverSkins (plugin)

BepInEx 6 IL2CPP plugin that loads custom skins (`*.hoskin`) into **HANDS OVER**. Skins are made with the
[HandsOverSkins SDK](../HandsOverSkins-SDK).

Features: custom wardrobe cards, Follow Rig creature skins (arm IK, rope hanging, jiggle), hand-cut stumps, ghost and
dead-body materials, Random card, favourites (star + pin to top), mid-match costume changes synced over chat, card holder
offsets, a public API for other mods.

## Requirements

| What | Version |
|---|---|
| HANDS OVER | current Steam version |
| BepInEx | 6.0.0 bleeding edge, Unity IL2CPP win-x64 (launch the game once so `BepInEx/interop` exists) |
| .NET SDK (to build) | 6.0+ |

## Build

```
dotnet build -c Release -p:GameDir="D:\SteamGames\steamapps\common\HANDS OVER"
```

- Copies `HandsOverSkins.dll` into `<Game>/BepInEx/plugins/HandsOverSkins/`.
- Writes the Thunderstore package to `bin/Thunderstore/<Author>-HandsOverSkins-<Version>.zip`
  (`-p:ThunderstoreAuthor=…`, `-p:ThunderstoreDeps=…`). Bump `<Version>` in the csproj and add a section to
  `Thunderstore/CHANGELOG.md` for every release.

## Layout

| Folder | Contents |
|---|---|
| `API/` | `SkinAPI`, `CustomSkin`, `SkinManifest` (the `.hoskin` JSON contract) |
| `Core/` | bundle loader, injector, shaders, ghost/dead materials, follow rigs, jiggle, stumps, held items, random, favourites, live changes |
| `Patches/` | Harmony patches |
| `Examples/` | example manifest and dependent plugin |
| `Thunderstore/` | README, CHANGELOG and icon for the package |
| `Tools/` | developer scripts used to read the game's IL2CPP metadata (paths inside are the author's) |

## API

```csharp
[BepInDependency(HandsOverSkins.Plugin.Guid)]
SkinAPI.LoadSkinBundle(path);
SkinAPI.RegisterSkin(new CustomSkin { … });
SkinAPI.TryGetSkin("yourname.ben_pirate", out var s);
SkinAPI.SkinRegistered += …; SkinAPI.SkinsInjected += …;
```
