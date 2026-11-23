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
4. In game: **M** toggles the map, **= / -** zoom. Settings are in `BepInEx\config\eidmods.core.cfg`.

Because this is co-op over Mirror, keep mods **client-side and read-only** where possible. Only the host is
authoritative, so anything that changes game state would need every player to run the mod.

## Hot reload (project layout)

The mod is split so feature changes don't need a game restart:

| Project | Deployed to | Reloadable? |
|---|---|---|
| `src/EIDMods`: the **loader**, the only BepInEx plugin. It owns the one injected MonoBehaviour (`HostBehaviour`) and passes Update/LateUpdate/OnGUI on to the features. | `BepInEx\plugins\EIDMods\` | No. The game locks it, so changes need a restart. Keep it small. |
| `src/EIDMods.Features`: **all mod features** (minimap, probe, ...). | `BepInEx\EIDMods\` (outside `plugins` so BepInEx doesn't scan it) | **Yes** |

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

## Minimap approach

**v2 (current): built from level collision data.** There's no camera, so the game's darkness doesn't matter
(`src/EIDMods.Features/Minimap/MapGrid.cs`).

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
- **Reference height:** cells are classified against a **reference height**, not your live feet height. The reference
  only moves once you've climbed or dropped more than 1 m. Classifying against live feet height made borderline cells
  flip between wall and floor as your height bobbed while walking.
- **Cell classification:** the world is split into 0.5 m cells, each checked against the reference height:
  1. A `CheckBox` at knee-to-head height (+0.6 to +1.6 m). If something is there, cast a ray down from +2.6 m. A
     walkable top (normal.y > 0.75) **above +0.5 m** means a **step up**. The height check matters: a ray that starts
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
  lines. F9 runs the level probe.

v1 used an orthographic camera rendering into a RenderTexture. It was dropped because HDRP rendered it with the
scene's real (dark) lighting.

Still to look at:
- **Zoom.** `ViewRadius` goes down to 5 m with `=`, which can hide every wall. Watch for this when "the map looks
  empty".
- **Fog of war / full map.** The map already shows only explored cells. Persisting them per level is a later step.
- **Teammates.** Show the other players as markers (`AG.Actors.Player.Players` lists them).
- **Performance.** Not measured yet. Composing costs about 65k grid lookups per frame, plus up to `SampleBudget` cells
  of physics queries.

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
