# HandsOverSkins

Custom skin loader for **HANDS OVER**. Adds skins made with the HandsOverSkins Unity SDK to every character's wardrobe,
and they work in multiplayer: everybody with the same skin installed sees it, everybody else sees the default.

## Features

- Loads `*.hoskin` skin bundles from `BepInEx/plugins` (any subfolder), so skin packs install like any other mod.
- Outfits, full-body replacements and **Follow Rig** creature skins (keep their own skeleton and proportions, arm IK, jiggle bones).
- Hand cutting, decapitation, ghost and dead-body materials work on custom skins.
- **Random** card per character (picks from unlocked, custom-only, favourites or all skins).
- **Favourites**: star any skin card in the wardrobe; starred skins are pinned to the top.
- **Change costume mid-match** with hotkeys (F6 previous, F7 next, F10 random, F11 random favourite), synced to other players running the mod.
- Per-skin card holder offsets for big hands and paws.

## Install

1. Install BepInEx 6 (IL2CPP, bleeding edge) for HANDS OVER.
2. Put this package's `plugins/HandsOverSkins` folder in `BepInEx/plugins`.
3. Add skin packs (`*.hoskin`) anywhere under `BepInEx/plugins`.

## Config

`BepInEx/config/com.handsover.customskins.cfg`: Random Skin pool, Live Change hotkeys and sync, Favourites, Follow Rig
options (arm IK, hanging, jiggle), auto ghost/dead materials, developer dump. Favourites are saved in
`BepInEx/config/HandsOverSkins.favorites.txt`.

## Mid-match changes and players without the mod

Live changes are announced through the game chat as a short `HOSKIN …` line. Players with this mod apply the change
and never see the line; players without it see the line once and keep seeing your old skin.

## Making skins

Use the HandsOverSkins SDK (Unity 6000.3, URP). See the SDK's tutorial.
