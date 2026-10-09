# OpenD2
A project to open-source Diablo 2, under the GNU General Public License.

### C# / Godot migration — M2 town and dungeon slice

The proposed C# and Godot migration architecture, asset compatibility requirements,
implementation backlog, and acceptance criteria are documented in
[the migration plan (한국어)](docs/migration/README.md).
The C# implementation in `src/` includes legacy asset readers and viewers, deterministic collision/combat/AI, and a synthetic town-to-dungeon quest with persistent in-session region state, loot, inventory and equipment-driven combat stats, and versioned checkpoints with backup recovery. See [build and run instructions](BUILDING.md) and [M2-05 results / remaining gates](docs/migration/M2_05_RESULTS.md). The game core runs locally without a central server; networked co-op and a dedicated .NET server host are planned for M5. Original game data acceptance, campaign gameplay and high-quality 3D presentation remain in progress. The C++ runtime remains separate.

The [PLAY integration work](docs/migration/PLAY_INTEGRATION.md) connects explicitly configured legacy DS1/DT1 terrain, DCC/COF actor artwork and click navigation to the local simulation. Diagnostic replay windows now roll without stopping gameplay. The Map tab can generate a single-region terrain scene from owned game data and explicit cell placements without hand-writing JSON. Generated actors start as placeholders. The Scene art tab configures each combat actor’s five DCC/COF motions and the guide NPC’s Idle artwork/fixed facing, previews explicit directions and saves a validated scene copy. The guide shares basic depth sorting and pauses its animation with game ticks; it does not walk or switch talking motions. Original audio still requires an authored mapping. Start-menu scene loading is available. Full legacy scenes require verified resource paths, coordinates and animation directions; preview combat/items do not yet reproduce the original rules. Real-data and visible gameplay acceptance remain pending.

[PLAY-11](docs/migration/PLAY_11_RESULTS.md) adds explicit DC6 HUD frames and placement in Scene art, a composite preview, health clipping and links to the existing menu/inventory controls. It does not implement original mana, skills, belt slots, fonts or the complete UI. LoD means the original **Lord of Destruction** expansion; its MPQ input is distinct from Resurrected's high-resolution 3D assets.

[PLAY-12](docs/migration/PLAY_12_RESULTS.md) connects explicitly configured DC6 item icons to the existing bag/equipment slots, with Scene art preview/save controls and text fallback. It retains the two preview item definitions; PLAY-14 below replaces the original eight-slot preview bag. Original combat/item rules and equipment-dependent character art remain pending.

[PLAY-13](docs/migration/PLAY_13_RESULTS.md) reads owned LoD item TXT definitions, validates type/slot references and lets Scene art search original codes/names and associate them with preview items and DC6 paths. The inventory shows base dimensions, requirements and ranges as reference information. Combat stats remain preview rules. PLAY-14 adds optional table-derived dimensions and grid/save migration.

[PLAY-14](docs/migration/PLAY_14_RESULTS.md) adds a 10×4 inventory with rectangular occupancy, atomic moves/swaps, drag/drop and keyboard cell selection. Scene art can opt into bound original item codes/sizes. Save schema 2 / rules 5 verifies and converts previous schema-1/rules-4 saves in memory, preserves the source until an explicit save, and refuses conversion if the bag cannot fit. Combat values remain synthetic.

[PLAY-15](docs/migration/PLAY_15_RESULTS.md) adds tick-based mana recovery, a selectable melee Power strike, Q/button casting, resource/cooldown feedback and optional DC6 Mana clipping. Save schema 3 / rules 6 verifies old v1/v2 files before in-memory conversion and preserves existing scene save paths. This is a preview skill, not the original skill tree, projectiles or D2R effects.

[PLAY-16](docs/migration/PLAY_16_RESULTS.md) adds health/mana potions, four belt slots, bag/belt swaps, 1–4 hotkeys and bounded consumption records. New monster kills retain their gear drop and add a potion. Full resources and rejected uses preserve the item. Save schema 4 / rules 7 verifies v1–v3 saves before upgrading, without moving existing v2/v3 items or resetting v3 mana. Recovery amounts and belt behavior remain preview rules.

See [release testing](docs/migration/RELEASE_QA.md) for packaged builds and the separate synthetic, legacy-scene and two-hour GUI acceptance cases. `v0.2.0-rc.3` includes the basic HUD, audio, inventory and startup menu. PLAY-08 through PLAY-16 require newer CI packages or a future release.

The historical image and CMake/TCP-IP instructions below describe the original C++ project. They are not evidence of C# GUI gameplay acceptance.

[M2-06a](docs/migration/M2_06A_RESULTS.md) opens the Simulation tab by default, adds a health/state HUD and saves FPS, fullscreen and diagnostics preferences. Use **Save settings** to keep changes, **F11** to toggle fullscreen, and **Show diagnostics** for seed/replay tools. Real GUI, DPI and fullscreen acceptance is pending.

[M2-06b1](docs/migration/M2_06B_RESULTS.md) adds optional scene PCM WAV effects/region music, bounded voices and saved audio levels/mute. The synthetic preview uses short generated effect tones; original audio listening acceptance is pending.

[M2-06d](docs/migration/M2_06D_RESULTS.md) adds a paused startup/session menu, in-memory continue and validated checkpoint loading. Choose **New game** to begin; **Menu (Esc)** returns to the menu.

[M2-06c](docs/migration/M2_06C_RESULTS.md) introduced eight bag slots (superseded by PLAY-14), weapon/body slots, item details, restart confirmation and an explicitly loaded remembered scene path. Save settings to retain the path.

![Diablo II Main Menu in OpenD2](https://i.imgur.com/RFNbRiT.png)

### Project Goals
Simply put, this project is a total rewrite of the game engine. It uses the original game files, and uses the original game's save files. Ideally it will also be compatible in TCP/IP games with the original client, but this may not be feasible.

Why would you want this? Well:
 * It will fix some bugs. However it will try to remain as close as possible to the original game experience.
 * It is a great base for building mods. In the past, mods relied on reverse engineering the game through hooking and memory patches to create advanced features.
 * It will run better than Blizzard's game, and won't require fiddling with Windows compatibility settings or running as Administrator to work.
 * It will run on Linux, Mac and Windows, without the need for emulators (ie, Wine)

It will not support Open or Closed Battle.net in order to minimize legal issues. Also, it will not support the cinematics because those use the proprietary BINK format.

### Project Status / Contributing
The majority of the gamecode is still being written. Currently, you can connect to a TCP/IP game and it will show up on the other end that you've connected, however it will stall on loading. Most of the main menu works outside of that.

If you would like to contribute to this project, please fork it and submit pull requests. 


### Compiling

#### Windows
To compile this project on Windows, all you will need is CMake and Visual Studio 2017 or later.

Run cmake-gui and set the Source directory to this folder. Set the "Where to build the files" to be ./Build. (This is so that the git repository doesn't pick this up as a source directory). Then, simply open the project file in whatever IDE you want.

#### Linux
Compilation on Linux requires the following prerequisites:
  * libglew-dev (or libglew-devel on Red Hat based systems)
  * libglm-dev (or libglm-devel on Red Hat based systems)
  * libsdl2-dev (or libsdl2-devel on Red Hat based systems)

After that, the following commands can be used to install:

```sh
mkdir build && cd build
cmake .. -DSDL2MAIN_LIBRARY=/usr/lib64/libSDL2.so
make
```

### Running
*Generally speaking* you will want to run the game from a separate directory from the main game, in order to not screw up your original installation.
The original game options are preserved:

* `-w` - Run in Windowed mode

And OpenD2 adds a few of its own, which start with `+` instead of `-`:

* `+basepath="..."` - Set the basepath (ie, where your game is installed to). Replace the ... with the path.
* `+homepath="..."` - Set the homepath (ie, where your game saves data to). Replace the ... with the path. Defaults to `<user>/My Documents/My Games/Diablo II`.
* `+modpath="..."` - Set the modpath (ie, where mods overwriting content will read from)
* `+sdlnoaccel` - Disables hardware acceleration
* `+borderless` - Run in borderless windowed mode
* `+logflags=...` - Set the priority for logging information. These are flags. 1 = Log Crashes, 2 = Log messages, 4 = Log debug info, 8 = Log system info, 16 = "prettify" the log

*Generally speaking*, you will want to run with `+basepath="C:/Program Files (x86)/Diablo II"` (assuming you have the default install directory)

In order to play, you must host a game in TCP/IP in vanilla Diablo 2 (version 1.10) and join it through the OpenD2 client. This is because OpenD2 does not have a serverside yet.

### Architecture
Just as in the original game, there are several interlocking components driving the game. The difference is that all but the core can be swapped out by a mod.

#### Core (game.exe)
The core game engine communicates with all of the other components and drives everything. It is (or will be) responsible for the following:
- Window management
- Filesystem
- Memory management
- Log management
- Archive (.mpq) management
- Networking
- Sound
- Rendering

#### Common Code (D2Common.dll)
D2Common contains common routines needed by both the serverside and clientside. This includes things such as dungeon-building from random seeds, skill logic, .TXT -> .BIN compilation, and more.

#### Serverside (D2Game.dll)
The serverside is responsible for quest management, AI, and more. Ideally, this should be allowed to mismatch the client DLL and have custom game server logic.

#### Clientside (D2Client.dll)
The clientside is responsible for client logic, mostly with drawing the menus and sprites.

The [NPC-01 Camp Guide preview](docs/migration/NPC_01_RESULTS.md) adds three scripted dialogue intents, bounded asynchronous inference, grounded responses and player-confirmed quest actions. It runs offline without a model. Actual LLM inference, persistent memory and open-world NPC planning remain in the [NPC roadmap](docs/migration/NPC_LLM_DESIGN.md).


[NPC-02a](docs/migration/NPC_02_RESULTS.md) adds an optional, source-built llama.cpp CPU runtime, verified/resumable Qwen model downloads, and Korean intent evaluation. Basic play stays model-free. This experimental adapter classifies intent; host-authored dialogue and explicit quest confirmation remain in control. Target-PC/GPU acceptance, freeform speech and persistent NPC memory remain open. See [BUILDING](BUILDING.md) for install/run/remove controls.
