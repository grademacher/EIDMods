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
  setup on **Unity 6000.3.15** (be.788). Our game is **6000.3.9**, which hasn't been tested. It might load fine, or
  it might hit the same crash. **Step 1 is to find out.**
- MelonLoader has open bugs for Unity 6000.3+/v39 ([#1159](https://github.com/LavaGang/MelonLoader/issues/1159),
  [#1229](https://github.com/LavaGang/MelonLoader/issues/1229)), so it's the fallback, not the default.

## Setup

1. Download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793+dbc82f0.zip` from https://builds.bepinex.dev/projects/bepinex_be
   and extract it into the game folder (so `winhttp.dll` sits next to `Expedition Into Darkness.exe`).
2. Launch the game once. The first launch takes a while because BepInEx generates `BepInEx\interop\*.dll`.
   Check `BepInEx\LogOutput.log`. If it reaches the main menu, the loader works.
3. (Recommended) Install **UnityExplorer** (IL2CPP / BepInEx 6 build) into `BepInEx\plugins`. Use it to inspect the
   live scene: the player object, the level geometry and its layers, and whether there's a NavMesh.
4. Build: `dotnet build src/EIDMods`. The build copies `EIDMods.dll` into `BepInEx\plugins\EIDMods\`.
   If your game path differs, create `Directory.Build.props.user` with a `<GamePath>` override.
5. In game: **M** toggles the map, **= / -** zoom. Settings are in `BepInEx\config\eidmods.core.cfg`.

Because this is co-op over Mirror, keep mods **client-side and read-only** where possible. Only the host is
authoritative, so anything that changes game state would need every player to run the mod.

## Minimap approach

**v1 (scaffolded):** An orthographic camera above the local player (`Mirror.NetworkClient.localPlayer`) renders into
a RenderTexture, which is drawn in the top-right corner. The near clip plane cuts off ceilings.

Expected problems to check in-game:
- **Darkness.** HDRP will render what the player sees, which is mostly black. Possible fixes: add an
  `HDAdditionalCameraData` with a fixed exposure or an override, or render only certain layers with a flat material.
- **HDRP cost.** A second HDRP camera is expensive. Use a lower resolution, render every N frames, and disable
  post-processing on it.

**v2 options** (these don't depend on lighting, and they look like a real "map"):
- **NavMesh.** If enemies use Unity NavMesh, `NavMesh.CalculateTriangulation()` gives the walkable floor plan.
  Draw it once to a texture and overlay the player position. Cheap and clean.
- **Collider slice.** Raycast or overlap the level's colliders on a grid around the player to build the walls.
- **Fog of war.** Reveal cells as the player walks through them, so the map shows only explored areas.
- **Teammates.** Show other `NetworkIdentity` players as markers.

## Reverse-engineering workflow

- Interop DLLs (`BepInEx\interop\Assembly-CSharp.dll`) in **ILSpy/dnSpy** show all class, field and method names.
  The method bodies are stubs.
- To see the actual logic, load `GameAssembly.dll` + `GameAssembly.pdb` in **Ghidra** or **IDA**. The PDB gives real
  function names.
- Use **UnityExplorer** at runtime to browse objects, components and fields live.
