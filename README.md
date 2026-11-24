# EIDMods — mods for *Expedition: Into Darkness*

Client-side BepInEx plugin. First feature: a **minimap** of the area around the local player.

## What the game is built on (checked 2026-11-15, Demo build 26386023, Steam app 5041080)

| | |
|---|---|
| Install | `D:\SteamLibrary\steamapps\common\Expedition Into Darkness Demo` |
| Engine | **Unity 6000.3.9f1**, **IL2CPP** (x64), D3D12 |
| IL2CPP metadata | **v39** (`global-metadata.dat` header `0x27`) |
| Render pipeline | **HDRP** (+ HBAO, DLSS/FSR plugins) |
| Networking | **Mirror** over **FizzySteamworks** (Steam P2P); also kcp2k/Telepathy present |
| Input | Legacy `UnityEngine.Input` (no new Input System package) |
| Audio | FMOD Studio (`StreamingAssets/*.bank`) |
| Content | Addressables (`StreamingAssets/aa`, ~21 GB) |
| Game code | `Assembly-CSharp.dll` (compiled into `GameAssembly.dll`) |
| Bonus | Dev shipped **`GameAssembly.pdb`** (843 MB). Full symbols, so Ghidra/IDA show real names for native code. |

Other notable assemblies: Cinemachine, RayFire (destruction), EdgeFusion, MagicFX5, Newtonsoft.Json.

## Mod loader choice: BepInEx 6 (bleeding edge), IL2CPP

Unity 6000.3 with metadata v39 is very new to the modding tools:

- **BepInEx 6 BE ≥ 755** can read v39 metadata (older builds fail with "We support 23-31, got 39").
  Latest is **be.793** (2026-11-05).
- An open Il2CppInterop issue ([#283](https://github.com/BepInEx/Il2CppInterop/issues/283)) reports crashes during hook
  setup on **Unity 6000.3.15** (be.788). **On our 6000.3.9 it works** (tested 2026-11-23): interop generates, plugins
  load, and injected MonoBehaviours run. The log still shows `Class::Init signatures have been exhausted, using a
  substitute!`. That's harmless so far, but it's the first suspect if Harmony patches on game methods start crashing.
- MelonLoader has open bugs for Unity 6000.3+/v39 ([#1159](https://github.com/LavaGang/MelonLoader/issues/1159),
  [#1229](https://github.com/LavaGang/MelonLoader/issues/1229)), so it's the fallback, not the default.

## Setup

1. Download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip` from https://builds.bepinex.dev/projects/bepinex_be
   and extract it into the game folder (so `winhttp.dll` sits next to `Expedition Into Darkness.exe`).
2. Launch the game once. The first launch takes a while because BepInEx generates `BepInEx\interop\*.dll`.
   Check `BepInEx\LogOutput.log`. If it reaches the main menu, the loader works.
3. Build: `dotnet build EIDMods.sln -c Release`. This deploys the loader to `BepInEx\plugins\EIDMods\` and the
   features to `BepInEx\EIDMods\` (see "Hot reload" below). If your game path differs, create
   `Directory.Build.props.user` with a `<GamePath>` override.
4. In game: **N** toggles the corner minimap, **M** opens the big map (Esc closes it), **= / -** zoom. Settings are in `BepInEx\config\eidmods.core.cfg`.

Because this is co-op over Mirror, keep mods **client-side and read-only** where possible. Only the host is
authoritative, so anything that changes game state would need every player to run the mod.

## Hot reload (project layout)

The mod is split so feature changes don't need a game restart:

| Project | Deployed to | Reloadable? |
|---|---|---|
| `src/EIDMods`: the **loader**, the only BepInEx plugin. It owns the one injected MonoBehaviour (`HostBehaviour`) and passes Update/LateUpdate/OnGUI on to the features. | `BepInEx\plugins\EIDMods\` | No. The game locks it, so changes need a restart. Keep it small. |
| `src/EIDMods.Features`: **all mod features** (minimap, map baker, probes, ...). | `BepInEx\EIDMods\` (outside `plugins` so BepInEx doesn't scan it) | **Yes** |
| `src/EIDMods.MapCore`: pure map math (heightfield, rasterizer, classifier, cache). No Unity references, unit-tested in `tests/EIDMods.MapCore.Tests` (`dotnet test tests/EIDMods.MapCore.Tests`). | Compiled into `EIDMods.Features.dll` by link, so it's still one reloadable DLL | **Yes** |

Workflow: edit a feature, then run `dotnet build src/EIDMods.Features -c Release`. The loader sees the new DLL and
reloads it within about a second. **F10** reloads by hand. Both are configurable in `BepInEx\config\eidmods.core.cfg`
(`[HotReload]`). The log shows `[HotReload] Loaded features #N (...)`.

How it works and its rules (see `src/EIDMods/IFeatureModule.cs`):
- The loader reads the DLL (and its pdb, for line numbers) **into memory** and loads it into a fresh
  `AssemblyLoadContext`, so the file is never locked. Old copies stay in memory, a few hundred KB per reload.
- Feature code **must not** call `ClassInjector.RegisterTypeInIl2Cpp`, because injected types can't be re-registered.
  Use the forwarded `Update`/`LateUpdate`/`OnGUI` instead.
- `Unload()` must destroy everything it created (GameObjects, textures). Harmony patches go through
  `FeatureContext.Harmony`, which the loader unpatches on reload.
- If feature code throws during a frame, the features are **paused**, not crashed. The error is logged once, and the
  next reload resumes them.
- Deploy compares file hashes, so a rebuild with no real change doesn't touch the deployed DLLs. If the loader really
  changed while the game is running, the build warns "Not deployed": close the game and build again.
- **Config entries must use types from outside the features DLL** (bool, float, KeyCode, ...). An enum declared in
  the features is a new type on every reload, BepInEx keeps the first binding, and the next `Bind` throws
  `InvalidCastException`, so the features fail to load.
- Each reload re-reads `eidmods.core.cfg`, so editing the file and pressing F10 applies the change.
- Background tasks must take a `CancellationToken` that `Unload()` cancels (see `MapBaker`).
- **Hot loops must not call `UnityEngine.Mathf` / `Color32.Lerp` / other Unity statics.** In IL2CPP each one is an
  interop call. Two `Mathf.FloorToInt` calls per minimap pixel cost about 13 ms per frame. Use `MathF` and plain
  arithmetic.

## Minimap approach

**v3 (current): a whole-level map baked once per level from collider geometry.** The bake is cached on disk and drawn
as a snapshot around you. Plan and evidence: `.project-memory/research/2026-11-23-minimap-geometry-bake-plan.md`.

- **What the levels are** (F7 bake probe, The Moor, 2026-11-23): hand-built scenes with sub-scenes (0 generator
  modules), with 74k static colliders: 16k boxes, 56k mesh colliders (**98.6 % readable from script**), and 1 terrain.
  `MapData.Preview` is 1080×1920 menu art, not a map.
- **Bake** (`MapBake/MapBaker.cs`): cache → collect → rasterize → cache.
  - **Collect**, on the main thread in `BakeBudgetMs` slices (`GeometryCollector`):
    - box colliders become boxes;
    - readable meshes are copied once per mesh;
    - everything else is measured per cell with `Collider.Raycast`;
    - colliders with a Rigidbody, triggers and terrain are skipped.
  - **Rasterize** on a background task into a **column heightfield**: per 0.5 m cell, the solid height spans
    (`MapCore`, Recast-style clip-to-cell). Convex colliders fill solid; non-convex meshes record only surfaces, so
    doorways under arches stay open.
  - **Cache** to `BepInEx\cache\EIDMods\maps\<MapID>_<version>_<buildGUID>.bin.gz`.
  - **The Moor:** about 1.3 s of main-thread work spread over about 12 s, then about 23 s in the background. The
    cache is 18 MB, and **a cache hit is ready in about 1 s**.
  - **Updates:** when more scenes load (sub-scenes streaming in), new colliders are baked incrementally (under 1 s).
    Hot reload and level change cancel a bake cleanly.
- **Drawing** (`Minimap/BakedMapView.cs`):
  - Cells are classified from the heightfield with the same rules as the physics scanner below (`ColumnClassifier`),
    relative to the reference height.
  - The result is despeckled and colored lazily per 32×32 chunk, nearest first, within a 2 ms budget. On a floor
    change, the old image stays up until the new one is built.
  - Terrain is sampled live (`Terrain.SampleHeight`).
- **Reveal:** by default only cells within `RevealRadius` (25 m) of where you've been are shown. Set
  `RevealFullMap = true` to show the whole level.
- **Cell occupancy inset:** a cell counts as occupied only if geometry reaches its inner part, at least 0.1 m in from
  each edge (`ClassifierBands.CellInset`). The bake and the overlay both use this rule, so a door frame that only
  grazes a doorway cell no longer closes it. Thin single planes in non-convex meshes keep the conservative rule.
- **Step-up reachability:** step-up (lighter "higher floor") is kept only where you can walk up to it from a floor,
  through neighbor steps of at most 0.6 m (`MapCore.StepReachability`). That covers stairs, ramps and rising ground.
  Raised tops you can't walk onto, like a railing's 1 m top rail, a table or a crate, are barriers and draw as
  **walls**. Low clutter with no flat top (barrels, debris) stays a dim obstacle.
- **Under the ground:** terrain counts as solid rock 50 m deep (so cliffs are walls), except in a column with a built
  walkable floor near your height more than 2 m below the terrain surface: a tunnel (the Moor's sewers), where the
  terrain is a ceiling.
- **Live overlay:** the physics scanner keeps running within 10 m of you (20 cells/frame, 2 s refresh). It overrides
  the bake **only when they disagree on blocked vs open** (`OverlayPolicy`):
  - A door opening or a wall being destroyed only counts once a full-cell check is completely clear.
  - Only a found **floor** opens a blocked cell. Physics sees terrain as a thin surface, so earth behind a sewer wall
    reads "clear, no floor" (void); letting that win drew a black disc around you in the Moor's sewers.
  - Disagreements about *which kind* of blocked are ignored. Physics reports "step up" on real walls when its ray
    starts inside one, and letting that win made catacomb walls vanish in sections as your height changed on slopes.
- **Before the first bake of a level finishes,** the physics scanner draws the whole map, with a
  "Mapping level… N %" label under it.
- **Cost:** about **1.3 ms/frame** steady (compose about 0.9 ms, overlay about 0.3 ms).
- **Accuracy:** about 90 % of random cells agree with the physics classifier. Most disagreements are physics
  artifacts (a ray that starts inside a solid falls through) or props with a Rigidbody, which the overlay covers.
- **Debug:** F4 dumps marker data (`[Markers]`), F5 dumps nearby colliders plus baked-vs-live cell maps (`[Nearby]`), F6 compares baked vs physics
  classification (`[Parity]`), F7 runs the bake probe, F8 runs `[MapDebug]`
  rays plus `[Perf]`, and F9 runs the level probe.

**Markers** (`src/EIDMods.Features/Minimap/Markers`, `MapCore/Markers`): dots drawn into the map image, so they
rotate and clip with it. Plan: `.project-memory/research/2026-11-23-minimap-markers-plan.md`.

| Marker | Look | Source |
|---|---|---|
| Teammate | green dot; **orange** when downed; arrow on the rim when off the map | `Player.Players` minus `Player.Local`; downed = state machine `Current` is `DownedState` |
| Enemy | red dot | live `AG.Enemies.Pawn`s in `Actor.All`, sampled at 10 Hz and interpolated |
| Container | small yellow dot, **hidden once opened** | `LootTableContainer` (`hasBeenOpened`), `LootContainer` |
| Corpse loot | small yellow dot | `PawnLootTableContainer` |
| Floor item | smallest yellow dot | `SingleLootItem` |
| Quest place | **cyan diamond**, rim arrow when far away, tick above/below when on another floor; text label on the big map | active objectives in your own save (`Stash.History.GetActive()`): `EnterLocation` → `LocationTrigger`, `InteractWithTarget` → `ObjectiveTarget` (same ID), `ExitThrough` → `ExitAreaComponent`/`Exitpoint` |
| Extraction | **violet square**, faded while an exit area is switched off; tick above/below on another floor; **hidden** (no arrow) when off the minimap, edge arrow on the big map; "Exit: <place>" label on the big map | `ExitAreaComponent` (walk-in zone, `State` on/off) and `Exitpoint` (interact exit, skipped while `IsHidden`), found once per level/scene load, state re-read every second |
| Quest enemy | red dot with a **cyan ring** (normal enemy distance rules) | pawns whose `Template` a `KillEnemy` objective asks for |

- **Settings:** `[Minimap.Markers]` holds `ShowPlayers`, `ShowEnemies`, `EnemyMode` (`near` within `EnemyRadius` 25 m
  through walls / `lineOfSight` / `all` / `off`), `ShowContainers`, `ShowCorpseLoot`, `ShowFloorItems`,
  `ShowOpenedLoot`, `ShowQuests`, `ShowExtraction` and `OtherFloorMarkers` (`fade`/`hide`, for enemies more than 3 m above or below you). **F3**
  toggles all markers.
- **Floors and distance:**
  - **Teammates** always show, on any floor and at any distance, with arrows at the rim.
  - **Loot** more than 2.5 m above or below you is always hidden. Faded loot from other floors looked like loot
    inside walls.
  - **Enemy and loot dots** near the rim are dropped rather than drawn over it.
- **Loot visibility:** loot only shows in explored cells (or everywhere with `RevealFullMap`).
- **Loot discovery:** `FindObjectsByType` costs 10–16 ms per type and can't be split across frames, so loot is found
  once per level and when new scenes load, one type per frame. Corpse loot and floor items are re-scanned 1 s after a
  nearby enemy dies. Opened or picked-up state is checked about 60 objects per frame.
- **Cost:** about 0.2 ms/frame with 18 loot dots on screen in Catacomb Ruins.
- **Quests:** objectives are re-read every 2 s; their places are looked up (one `FindObjectsByType` per frame) only
  when the set of place objectives changes or scenes load. Item objectives (collect, hand over, craft) have no place.
  Objectives whose `MapRequirements` don't include the current map are skipped.
- **F4** dumps what the providers see, including active quests and their objectives.

**Big map** (`Minimap/BigMap.cs`, `MapCore/BigMapCamera.cs`, `MapCore/FloorLevels.cs`). **M** opens a large north-up
panel; **Esc** or **M** closes it, and the corner minimap is hidden meanwhile.
- **Controls:** drag with the left mouse to move, wheel or **= / -** to zoom (5–300 m), **C** or middle click to
  recentre on you.
- **Elevation:** **PageUp/PageDown** jump between floors (your own height counts as a stop, so PageUp undoes
  PageDown; with nothing further they move 3 m, within 30 m of you), the slider beside the panel moves smoothly (it snaps within
  0.4 m of a floor), **Home** follows you again. Floors are the busiest walkable heights within 120 m of you (at least
  2.5 m apart, recomputed after you move 60 m), kept within 30 m above/below you.
- **Data:** its own `BakedMapView` at the chosen height (the minimap's cache is untouched); it follows
  `RevealFullMap` like the minimap. Redraws at 10 Hz, or immediately while you pan, zoom or change height. No cost
  while closed. While it follows your height it copies the minimap's live door/destruction corrections twice a
  second; at another height it shows the bake only. Quest labels say how far above/below a place on another floor is.
- **Input while open (local only):** the cursor is freed, `AG.Core.Keybinds.IsBlocked` is held on (no attacks), and
  the camera's look sensitivity is held at 0 (the game's block doesn't cover mouse look). All are restored on close,
  with the camera held for 3 more frames because re-locking the cursor reads as a mouse flick.

**v2: physics scanner** (`src/EIDMods.Features/Minimap/MapGrid.cs`). It is now the pre-bake fallback and the live
overlay, and its rules are the classification rules for both:

- **What level data exists** (from the F9 level probe in the `Tutorial` scene, 2026-11-23):
  - **Generator modules:** 0. `AG.Generation.Module` exists in code, but this level was built by hand.
  - **NavMesh:** present (`SamplePosition` works), but `CalculateTriangulation`/`Triangulate` are stripped, so the
    whole mesh can't be read.
  - **Colliders:** plenty of level geometry, all static. That's what the map uses.
- **Layers:**
  - **Used** (level geometry): 0 Default (incl. Terrain), 7 Always Render, 9 Small, 10 Medium, 13 Distant,
    14 Terrain. Small (9) counts for floor height only, never as a wall or obstacle, because small props were the
    main source of single-cell noise.
  - **Ignored:** 6 ClothCollision (your own cloth colliders sit 0.2 m away), 19 Hitbox, 21 Pawn, 22 Actor,
    23 Trigger, 11/12 Items, ragdoll layers, 4 Water, 20 Navigation Obstacle.
- **Reference height** (`MapCore.ReferenceHeight`): cells are classified against a **reference height**, not your
  live feet height.
  - It follows you once you've moved **more than 0.5 m up or down**, so small height bobs don't re-classify the map.
    Classifying against live feet height made borderline cells flicker.
  - The body band starts at +0.45 m, not knee height, so a 1 m railing still reads as a barrier even with the
    reference 0.5 m above your feet. The old 1 m lag let a railing on a landing you'd walked down to draw as floor.
- **Cell classification:** the world is split into 0.5 m cells, each checked against the reference height:
  1. A `CheckBox` at body height (+0.45 to +1.6 m). If something is there, cast a ray down from +2.6 m. A
     walkable top (normal.y > 0.75) **above +0.45 m** means a **step up**. The height check matters: a ray that starts
     inside a tall wall mesh doesn't hit it from the inside, falls through to the ground behind it, and would
     otherwise look like floor. Otherwise a second `CheckBox` at head height (+1.7 to +2.3 m) separates a **wall**
     (tall) from an **obstacle** (knee- or waist-high clutter, drawn dimmer).
  2. Otherwise, cast a ray down from +0.6 m. Floor within ±0.5 m is one flat color; beyond that it's shaded by height
     (lighter = higher). A drop of more than 1.5 m reads as a lower floor, and no hit at all means a **void**.
- **Despeckle:** what's drawn is a majority-filtered copy of the grid. A cell that matches fewer than 2 of its 8
  neighbors takes the most common neighboring type. It's rebuilt only for chunks whose cells changed. A stronger
  filter would erase one-cell-wide walls.
- **Exploration:** cells are sampled nearest-first, up to `SampleBudget` per frame within `SampleRadius`, and kept as
  you explore. A cell is re-sampled after `RefreshSeconds` (doors) or when the reference height changes (another
  floor). A new character means a new level, so a fresh map.
- **Drawing:** a round 256 px image composed on the CPU each frame and rotated with the view camera. One bulk memcpy
  goes into an IL2CPP array, then `SetPixels32` + `Apply`. The marker's dot is you, and its tick shows which way your
  character faces.
- **Who it follows:** `AG.Actors.Player.Local`, the game's own static for your character. If that isn't set, it falls
  back to `Mirror.NetworkClient.localPlayer`. Never `Camera.main`: it's a third-person camera behind the character.
- **Debug:** F8 logs the cell classification along 8 directions, both live and as stored in the grid, as `[MapDebug]`
  lines.

v1 used an orthographic camera rendering into a RenderTexture. It was dropped because HDRP rendered it with the
scene's real (dark) lighting.

Still to look at:
- **Zoom.** `ViewRadius` goes down to 5 m with `=`, which can hide every wall. Watch for this when "the map looks
  empty".
- **Hills read as walls.** Terrain more than head height above you is classified as a wall relative to your level, so
  in a valley the hillsides look like a wall band. Showing high ground as shaded ground instead is a design change.
- **Style pass.** Anti-aliased wall outlines, soft edges, icons.
- **Teammates.** Show the other players as markers (`AG.Actors.Player.Players` lists them).
- **Persisting explored areas** per level across visits.

## Reverse-engineering workflow

- Interop DLLs (`BepInEx\interop\Assembly-CSharp.dll`) in **ILSpy/dnSpy** show all class, field and method names.
  The method bodies are stubs.
- To see the actual logic, load `GameAssembly.dll` + `GameAssembly.pdb` in **Ghidra** or **IDA**. The PDB gives real
  function names.
- **`utils/MetaDump`** quickly searches the interop assemblies by name, without opening a decompiler:

  ```
  dotnet run --project utils/MetaDump -- "<game>\BepInEx\interop\Assembly-CSharp.dll" "<type regex>" ["<member regex>"]
  ```

  It prints each matching type with its base class, plus any fields, properties and methods that match the member
  regex. For example, `"^AG\.Actors\.Player$" "(?i)local"` is how `Player.Local` was found.
- **UnityExplorer does not work** on this Unity version (v4.13.6 tested 2026-11-23). Unity 6.3 changed scene handles
  from `int` to a `SceneHandle` struct, and its UI bundle fails to load, so it never initializes and F7 does nothing.
  Don't install it.
