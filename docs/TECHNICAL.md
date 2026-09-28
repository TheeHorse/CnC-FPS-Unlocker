# How RA3 High FPS works

Notes for anyone who wants to build on this (hi, C&C:Online folks). Addresses are for the current Steam `RA3_1.13.game` (EA's 2025 rebuild, PE timestamp `0x67B7A95B`). The launcher finds everything by byte signature, so small updates might still work. Run it with `--check` to see.

## The basics

- The render rate lives at `0xCB8514` (int, stock 30) and the logic rate at `0xCB8510` (int, stock 15). Logic and render are already separate in this build, with 2 render frames per logic tick at stock.
- Game speed is driven by the logic rate. Changing only the render side keeps game speed normal.
- Changing the logic rate does speed the game up. I tested it, so don't bother.

## Why nothing on disk is changed

Tacitus only accepts executables it knows, so a patched exe gets rejected. The launcher does this instead:

1. Starts the stock game with `CREATE_SUSPENDED` and the same `-config "<SkuDef>"` command line RA3.exe would use.
2. Allocates a small block in the game process, writes the chosen fps there, and repoints some `[0xCB8514]` operands at it.
3. Resumes the game and waits for it to exit, so Steam still shows you in-game.

## Which reads get redirected (the important part)

The render rate gets read in 119 places. It's used for two different things:

**1. Actual frame pacing.** These need the new fps:
- the frame limiter, `frame_ms = 1000 / fps` (`0xBBBC00`)
- the main loop's frames-per-logic-tick, `fps / logic` (`0x63F4F6`), and a matching check (`0x628C90`)
- a few static initializers and object fields that pace frames: ms per frame as a float, fps as a float, etc.

**2. A time unit, "30ths of a second", used by effects.** These must stay at 30:
- A static initializer builds `framesPerMs = fps * 0.001` (`0xCE511C`), and the particle code (`0x6EA76A`) uses it to stamp particle birth times.
- The particle manager's "now" is `clientTimeMs * fps * 0.001` (`0x6F6DF0`).
- Meanwhile the GPU particle shader gets its clock from the effect parameter `Time`, set at `0x4DA810` as `clientTimeMs / 1000`. A preshader multiplies it by a fixed 30. So the shader always counts in 30ths.

If you redirect the group 2 reads, CPU-side birth times run `fps/30` times ahead of the shader clock. The shader computes `age = c38.x - birth` (for example `296.88 - 1186.6`), gets a big negative age, and the size (`b^age`) and color keyframe math extrapolate into screen-covering quads. That's the white or colored flicker every high-fps attempt for this game seems to hit. It's most visible near water, because the particles get drawn into the 512x512 water reflection target, and the water plus bloom smears it across the screen.

The launcher's default is "all core pacing sites except the `framesPerMs` initializer" (`--core 0-4,6,7`). It also runs the particle simulation call (`0x61D2BD` -> `0x6FAF80`) at 30 Hz through a small accumulator stub. That keeps the particle simulation at its stock rate. Only the simulation step is throttled: the per-frame draw-bucket rebuild in the same function has to keep running, or you get stale pointers and a crash.

## How the flicker got found

Guessing didn't work: tick rate, frame_ms conversions, 1/30 and 30.0 constants and the particle step rate all made no difference. What worked:

1. Capture a campaign session with apitrace's `d3d9.dll` wrapper (`--trace G:\somewhere\ra3.trace` makes the launcher set `TRACE_FILE`).
2. Replay it with `d3dretrace -S "*/frame"` to get a snapshot of every frame, then score frames that differ from both neighbours while the neighbours match each other. The flashes turned out to be periodic, about once per logic tick.
3. Snapshot every draw call (`-S "<range>/draw"`) in one bad frame and the good frame after it, then diff them draw by draw. They were identical up to draw #570, a 4-particle GPU batch drawn into the water reflection.
4. Everything bound for that draw was identical between the two frames (shader, textures, render states, vertex data), except the shader clock `c38`. The vertex data's birth times were exactly 4.00x the clock at 120 fps, which pointed straight at the fps-derived time conversion.

## Known leftovers

- Particles simulate at 30 Hz (like stock), so smoke motion is a little less smooth than units. Try `--pfx off` if you want to experiment.
- A small UI pulse calculation reads the render rate in a form I didn't bother redirecting. I haven't seen it matter.
- Only tested on the Steam 1.13 build. 1.12 has the same code layout and the signatures match there too, but I haven't played it.
