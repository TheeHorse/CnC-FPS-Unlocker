# C&C FPS Unlocker

Play **Red Alert 3**, **C&C 3: Tiberium Wars** and **Kane's Wrath** at 60, 120, 165 or whatever fps you want. Free, open source, works with Tacitus / C&C:Online.

These games are locked to 30 fps and if you just unlock them the whole game speeds up. This keeps the game speed normal and only makes it smoother. No effect flicker either.

![Setup window](docs/screenshot.png)

## Install

Two ways, pick whichever you like. Either way there's no launch option to set, you just play the game like normal (Steam, EA app, disc, whatever).

**With the setup:** grab `CnC-FPS-Unlocker-Setup.exe` from [Releases](../../releases) and run it. It finds your games, pick a frame rate and hit **Next**. If it doesn't find a game (EA app, Origin, disc in a weird spot) use **Add game folder...** and pick the game's install folder.

**By hand, no exe at all:** grab `CnC-FPS-Unlocker.zip` from [Releases](../../releases) and copy the three files for your game next to the game's actual exe:

| Game | Files | Put them in |
|---|---|---|
| Red Alert 3 (1.12 / 1.13) | `d3d9.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<RA3>\Data\` |
| Tiberium Wars (1.10) | `dinput8.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<TW>\RetailExe\1.10\` |
| Kane's Wrath (1.3) | `dinput8.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<KW>\RetailExe\1.3\` |
| Kane's Wrath (1.02, EA app / Origin / disc) - beta | `dinput8.dll`, `CnCFpsUnlocker.dll`, `RA3HighFps.ini` | `<KW>\RetailExe\1.2\` |

To change the fps run the setup again or edit `RA3HighFps.ini` (the zip has ready-made ones for each fps in `FPS presets`). To uninstall delete `d3d9.dll` / `dinput8.dll`.

**Coming from v1.6 or older?** You don't need the Steam launch option anymore, clear it (right-click the game > Properties > Launch Options). Leaving it is harmless, it only patches once.

Tested on the **Steam** versions (RA3 1.13, Tiberium Wars 1.10, Kane's Wrath 1.3). If it doesn't work on your copy, open an issue with your `%TEMP%\RA3HighFps.log`, it has a report that helps me add support.

## Picking a frame rate

Has to be a multiple of 15 (60, 75, 90, 120, 135, 165, 240...). The game ticks 15 times a second so every tick needs a whole number of frames or the speed drifts. 144hz monitor? Use 135. It won't go above your monitor's refresh rate anyway since the game can't draw faster than your screen.

**Game speed stays right even if your PC can't keep up.** Game logic runs off the clock now instead of counting frames, so if you set 120 and only get 80 the game still runs at normal speed, it just looks a bit less smooth.

Before v1.6 that wasn't true. RA3 ran about 33% fast at 120 and way faster at 240 (the engine was only made to go up to 90), TW and KW went slow-mo whenever your fps dropped below the target, and every setting was a few % fast because of a rounding thing in the frame limiter.

## Known issues

- **240 fps is experimental** until it's been tested more, some people have had problems with it. If you're on a 240hz monitor and something's weird try 165 or 120
- Some effects still play too fast (like the RA3 power plant fog) and the TW/KW Ion Cannon hit looks off ([#4](../../issues/4), [#5](../../issues/5))


## Extras

Stuff that isn't about frame rate. Off unless you turn it on in the setup (or the ini).

- **Camera zoom-out (RA3):** lets you zoom out further (1.25x, 1.5x or 1.75x). Only in skirmish and campaign, online and LAN always use normal zoom so nobody gets an advantage. Past 1.75x the ground stops drawing at the top of the screen so that's the max for now

## Is it safe?

- It doesn't touch any game files. The game loads the dll from its own folder like any other dll mod, the dll changes a few timing values in the game's memory while it's starting, and that's it. No network code, nothing runs outside the game
- The setup is optional and only copies files. If you don't want to run an exe, install by hand
- Everything's built from the source here: [`src/Unlocker.cs`](src/Unlocker.cs) (the fixes, C#), [`installer/setup.iss`](installer/setup.iss) (the setup, made with [Inno Setup](https://jrsoftware.org/isinfo.php)) and [`dll/proxy.c`](dll/proxy.c) (the small `d3d9.dll` / `dinput8.dll`, about 150 lines). Run `build-from-source.bat` to build the dll yourself with the C# compiler that comes with Windows, and `dll/build.bat` for the dlls (needs MinGW)

## Online

A C&C:Online admin told me it won't get anyone banned. Only the rendering changes, game logic is the same as everyone else's. Still, use it online at your own risk and ask on their Discord if you're not sure.

## How it works (short version)

These games use the number 30 for two different things: how often they draw a frame, and as a clock for effects (particles count time in 30ths of a second). Old unlock methods change both, so particles get birth times from the "future" and you get huge white or colored flashes whenever something shoots, especially near water. This only changes the first one.

It finds what to patch by what the code does instead of hardcoded addresses, which is why the same dll works for all three games.

The full story, including how I tracked down the flicker with a frame by frame graphics capture, is in [docs/TECHNICAL.md](docs/TECHNICAL.md).

## What about other C&C games?

- **Generals / Zero Hour**: logic and rendering use the same clock so this trick doesn't work. Check out [TheSuperHackers/GeneralsGameCode](https://github.com/TheSuperHackers/GeneralsGameCode), they work from EA's released source
- **Red Alert 2 / Tiberian Sun**: totally different engine, same problem
- **RA3 Uprising**: the setup finds it and it should work (same engine) but I haven't been able to test it. Let me know!

## Older versions and mods

Since the game loads the dll itself, mods and older versions just work the normal way: `-ui` (the RA3 launcher window), `-runver 1.12`, `-modConfig "...\mod.skudef"` and mod launchers like GenEvo's all still get the fps fix. RA3 1.12 and 1.13 both live in `Data\`, so one install covers both.

## Credits

- [red-alert-3-60fps-mod](https://github.com/isma3iloiso/red-alert-3-60fps-mod) by isma3iloiso, their patch notes pointed me at the render fps value in the first place
- [CNCStuff/cnc3_fps_patch](https://github.com/CNCStuff/cnc3_fps_patch), which found some of the C&C3 fixes (frame limiter, scroll, anim timing)
- [apitrace](https://github.com/apitrace/apitrace), couldn't have found the flicker without it
- Not affiliated with EA or C&C:Online. Command & Conquer, Red Alert and Tiberium are trademarks of Electronic Arts

GPL v3 (see LICENSE). Use it, share it, change it, but anything built from this code has to stay open source under the same license and keep the credit. Versions up to v1.5 were MIT.

- TheeHorse
