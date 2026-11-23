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

**v1 (working, verified in a dungeon 2026-11-23):** An orthographic camera above the local player renders into a
RenderTexture, which is drawn in the top-right corner. The near clip plane cuts off ceilings.
- **Who it follows:** `AG.Actors.Player.Local`, the game's own static for your character. If that isn't set, it falls
  back to `Mirror.NetworkClient.localPlayer`, then to `Camera.main` (in menus). It re-checks every second.
  Don't follow `Camera.main` in game: it's a third-person camera behind the character, so the map ends up off-center.
- **Rotation:** "up" on the map is where the view camera looks, not where the character's body faces.

Still to look at:
- **Darkness.** HDRP renders the map with the scene's real lighting, so unlit areas show up dark. Possible fixes: add
  an `HDAdditionalCameraData` with a fixed exposure, or render only certain layers with a flat material.
- **HDRP cost.** A second HDRP camera is expensive. Use a lower resolution, render every N frames, and disable
  post-processing on it.

**v2 options** (these don't depend on lighting, and they look like a real "map"):
- **NavMesh.** If enemies use Unity NavMesh, `NavMesh.CalculateTriangulation()` gives the walkable floor plan.
  Draw it once to a texture and overlay the player position. Cheap and clean.
- **Collider slice.** Raycast or overlap the level's colliders on a grid around the player to build the walls.
- **Fog of war.** Reveal cells as the player walks through them, so the map shows only explored areas.
- **Teammates.** Show the other players as markers (`AG.Actors.Player.Players` lists them).

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
