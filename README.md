# RA3 FPS Unlocker

Play Command & Conquer: Red Alert 3 at 60, 120, 165, whatever fps. Free, open source, and it works with Tacitus / C&C:Online.

RA3 is locked to 30 fps, and just unlocking it makes the whole game run faster. This keeps the game at normal speed and only makes it smoother.

![Setup window](docs/screenshot.png)

## Install

1. Download `RA3-FPS-Unlocker-Setup.exe` from [Releases](../../releases).
2. Double-click it. It finds your Red Alert 3 folder, you pick a frame rate from the dropdown, and you hit **Install**.
3. It copies a line to your clipboard. In Steam, right-click Red Alert 3 > **Properties** > **Launch Options** and paste it. You only do this once.
4. Hit Play in Steam like normal.

To change the fps later, run the setup again, or edit `RA3HighFps.ini` in your RA3 folder.
To uninstall, clear the Launch Options box in Steam.

Needs the **Steam** version of RA3 (the current 1.13 update). Works with or without Tacitus.

## Picking a frame rate

It has to be a multiple of 15 (60, 75, 90, 120, 135, 165, 240...). The game ticks 15 times a second, so each tick needs a whole number of frames or the game speed drifts. If you have a 144 Hz monitor, use 135.

## Is it safe?

- It doesn't modify any game files. It starts the normal game, changes one timing value in memory while it's starting up, and that's it.
- The source is right here in `src/RA3HighFps.cs`, about 700 lines of plain C#. If you don't trust the exe (fair), download the repo and run `build-from-source.bat` to build it yourself. It uses the C# compiler that already comes with Windows, no downloads needed.
- Some antivirus programs don't like unsigned exes that touch another program's memory. That's why the source is public.

## Online

A C&C:Online admin told me it won't get anyone banned. Only the rendering changes, and game logic is identical to everyone else's. I'd still say use it online at your own risk, and ask on their Discord if you're unsure.

## How it works (short version)

RA3 uses one number, 30, for two different things: how often it draws a frame, and as a clock for effects (particles count time in 30ths of a second). Old unlock methods change both. Then particles get stamped with birth times from the "future", and you get giant white or colored flashes whenever something shoots, especially near water. This only changes the first one.

The full story, including how I tracked the flicker down with a frame-by-frame graphics capture, is in [docs/TECHNICAL.md](docs/TECHNICAL.md).

## Advanced options

Put these in the Steam launch options, before `%command%`:

- `--fps 90` overrides the ini for one launch
- `--pfx off` stops particles from simulating at 30 Hz (they look a bit smoother, but I haven't tested it much)
- `--check "<path to RA3_1.13.game>"` is a dry run that writes a report of which code locations it would patch. Useful if an update breaks it.

## Credits

- [red-alert-3-60fps-mod](https://github.com/isma3iloiso/red-alert-3-60fps-mod) by isma3iloiso, whose patch notes pointed me at the render fps value in the first place.
- [apitrace](https://github.com/apitrace/apitrace), which made it possible to find the flicker.
- Not affiliated with EA or C&C:Online. Command & Conquer and Red Alert are trademarks of Electronic Arts.

MIT license. Do whatever you want with it.

— TheeHorse
