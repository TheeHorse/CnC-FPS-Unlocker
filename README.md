# C&C FPS Unlocker

Play **Red Alert 3**, **C&C 3: Tiberium Wars** and **Kane's Wrath** at 60, 120, 165 or whatever fps you want. Free, open source, works with Tacitus / C&C:Online.

These games are locked to 30 fps and if you just unlock them the whole game speeds up. This keeps the game speed normal and only makes it smoother. No effect flicker either.

![Setup window](docs/screenshot.png)

## Install

1. Grab `CnC-FPS-Unlocker-Setup.exe` from [Releases](../../releases)
2. Run it. It finds your games, untick any you don't want, pick a frame rate and hit **Install**
3. It gives you one line per game. In Steam right-click the game > **Properties** > **Launch Options** and paste it in (there's a Copy button). Only have to do this once
4. Play from Steam like normal

To change fps later just run the setup again, or edit `RA3HighFps.ini` in the game folder.
To uninstall, clear the launch options box in Steam.

Tested on the **Steam** versions (RA3 1.13, Tiberium Wars 1.10, Kane's Wrath 1.3).

**EA app / Origin / other non-Steam copies:** if the setup doesn't find your game, use **Add game folder...** and pick the game's install folder. Non-Steam games get the drop-in version installed (see below), so there's no launch option to set, just start the game like normal. If the game folder is in Program Files you might need to run the setup as administrator.

### Drop-in version (no installer, no exe) - testing

This one's still being tested on non-Steam copies so treat it as a beta for now. If something's off open an issue.

For non-Steam copies or if you'd rather just copy files: grab `CnC-FPS-Unlocker-DropIn.zip` from [Releases](../../releases) and put the three files for your game next to the game's actual exe:

| Game | Files | Put them in |
|---|---|---|
| Red Alert 3 (1.12 / 1.13) | `d3d9.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<RA3>\Data\` |
| Tiberium Wars (1.10) | `dinput8.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<TW>\RetailExe\1.10\` |
| Kane's Wrath (1.3) | `dinput8.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<KW>\RetailExe\1.3\` |

Then launch the game however you normally do. Settings are in `RA3HighFps.ini` (the zip has ready-made ones for each fps in `FPS presets`), and to uninstall just delete the dll. If it doesn't work on your copy, open an issue with your `%TEMP%RA3HighFps.log`, it has a report that helps me add support. The game loads the dll from its own folder, it passes everything through to the real Windows one and does the same fixes as the normal version. Works with Tacitus, and having both this and the Steam launch option is fine, it only patches once. Source is in [`dll/`](dll).

## Picking a frame rate

Has to be a multiple of 15 (60, 75, 90, 120, 135, 165, 240...). The game ticks 15 times a second so every tick needs a whole number of frames or the speed drifts. 144hz monitor? Use 135. It won't go above your monitor's refresh rate anyway since the game can't draw faster than your screen.

**Game speed stays right even if your PC can't keep up.** Game logic runs off the clock now instead of counting frames, so if you set 120 and only get 80 the game still runs at normal speed, it just looks a bit less smooth.

Before v1.6 that wasn't true. RA3 ran about 33% fast at 120 and way faster at 240 (the engine was only made to go up to 90), TW and KW went slow-mo whenever your fps dropped below the target, and every setting was a few % fast because of a rounding thing in the frame limiter.

## Known issues

- **240 fps is experimental** until it's been tested more, some people have had problems with it. If you're on a 240hz monitor and something's weird try 165 or 120
- Some effects still play too fast (like the RA3 power plant fog) and the TW/KW Ion Cannon hit looks off ([#4](../../issues/4), [#5](../../issues/5))


## Extras

The setup has an **Extras...** button for stuff that isn't about frame rate. All off unless you turn them on.

- **Camera zoom-out (RA3):** lets you zoom out further (1.25x, 1.5x or 1.75x). Only in skirmish and campaign, online and LAN always use normal zoom so nobody gets an advantage. Past 1.75x the ground stops drawing at the top of the screen so that's the max for now

## Is it safe?

- It doesn't touch any game files. It starts the normal game, changes a few timing values in memory while it's starting, and that's it
- Source is right here in `src/RA3HighFps.cs`, plain C#. If you don't trust the exe (fair enough) download the repo and run `build-from-source.bat` to build it yourself. It uses the C# compiler that already comes with Windows and builds both `RA3HighFps.exe` and the `CnCFpsUnlocker.dll` from the drop-in zip (same source file, the dll is just the library version). The small `d3d9.dll` / `dinput8.dll` are built from [`dll/proxy.c`](dll/proxy.c), about 150 lines, with `dll/build.ps1`
- Some antivirus programs don't like unsigned exes that mess with another program's memory. That's why the source is public

## Online

A C&C:Online admin told me it won't get anyone banned. Only the rendering changes, game logic is the same as everyone else's. Still, use it online at your own risk and ask on their Discord if you're not sure.

## How it works (short version)

These games use the number 30 for two different things: how often they draw a frame, and as a clock for effects (particles count time in 30ths of a second). Old unlock methods change both, so particles get birth times from the "future" and you get huge white or colored flashes whenever something shoots, especially near water. This only changes the first one.

It finds what to patch by what the code does instead of hardcoded addresses, which is why one exe works for all three games.

The full story, including how I tracked down the flicker with a frame by frame graphics capture, is in [docs/TECHNICAL.md](docs/TECHNICAL.md).

## What about other C&C games?

- **Generals / Zero Hour**: logic and rendering use the same clock so this trick doesn't work. Check out [TheSuperHackers/GeneralsGameCode](https://github.com/TheSuperHackers/GeneralsGameCode), they work from EA's released source
- **Red Alert 2 / Tiberian Sun**: totally different engine, same problem
- **RA3 Uprising**: the setup finds it and it should work (same engine) but I haven't been able to test it. Let me know!

## Older versions and mods

Easiest way: tick **"Show a mod & version picker when the game starts"** in the setup. Every time you launch you get a little window to pick a mod (it lists what's in `Documents\Red Alert 3\Mods`, or browse to a `.skudef`) and a game version. On "Auto" it uses whatever version the mod wants, so mods that need 1.12 just work. It remembers what you picked last.

The normal RA3 launcher options still work too, put them **after** `%command%` in the Steam launch options:

- `-runver 1.12` runs 1.12 instead of the newest version (lots of mods need 1.12)
- `-modConfig "C:\path\to\mod\mod.skudef"` loads a mod

Like this for a mod that needs 1.12:

```
"...\RA3HighFps.exe" %command% -runver 1.12 -modConfig "C:\path\to\mod\mod.skudef"
```

`-ui` (the old launcher window) doesn't work since the unlocker replaces that launcher, use the options above instead. Mod launchers that start the game themselves skip Steam, so the unlocker won't run with those.

## Advanced options

These go in the Steam launch options, before `%command%`:

- `--fps 90` overrides the ini for one launch
- `--lang german` forces a language. Normally it uses whatever language Steam installed, then your Windows language
- `--pfx off` lets RA3's particles simulate faster than 30hz (a bit smoother, not tested much)
- `--check "<path to the game's .game/.dat exe>"` dry run, writes a report of what it would patch. Handy if an update breaks it

## Credits

- [red-alert-3-60fps-mod](https://github.com/isma3iloiso/red-alert-3-60fps-mod) by isma3iloiso, their patch notes pointed me at the render fps value in the first place
- [CNCStuff/cnc3_fps_patch](https://github.com/CNCStuff/cnc3_fps_patch), which found some of the C&C3 fixes (frame limiter, scroll, anim timing)
- [apitrace](https://github.com/apitrace/apitrace), couldn't have found the flicker without it
- Not affiliated with EA or C&C:Online. Command & Conquer, Red Alert and Tiberium are trademarks of Electronic Arts

GPL v3 (see LICENSE). Use it, share it, change it, but anything built from this code has to stay open source under the same license and keep the credit. Versions up to v1.5 were MIT.

- TheeHorse
