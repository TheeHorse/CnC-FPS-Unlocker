# How C&C FPS Unlocker works

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

## Tiberium Wars and Kane's Wrath

C&C3 and Kane's Wrath have the same design: render 30 and logic 15 sitting next to each other, the same frame limiter, the same sub-step main loop and the same static initializers, including the `framesPerMs = fps * 0.001` one that the particle code uses. The code is compiled differently though, so exact byte signatures don't carry over. Since v1.1 the launcher finds the pacing reads by behaviour:

| What | How it's recognised |
|---|---|
| frame limiter | `mov eax,1000 / xor edx,edx / div [fps]` |
| frames per logic tick | `mov eax,[fps] / xor edx,edx / div [logic]` followed by a compare against 6 (`cmp reg,6`, or `push 6 ... cmp`) |
| the 64000 site | `mov reg,[fps]` with `mov eax,64000` right after |
| static initializers | `fild [fps]` followed by `fdivr` (1000/fps) or `fstp` (fps). Anything followed by `fmul` is a time conversion and is left alone. |
| object field | the single `mov eax,[fps] / mov [esi+x],eax` in a constructor |

On RA3 this picks exactly the 7 sites the hand-tuned version used. Addresses found:

- **Tiberium Wars** (`RetailExe\1.10\cnc3game.dat`): render `0xB877C4`, sites `0x548132 0x55D94F 0x55DDDF 0x865660 0xA24B9D 0xA24BE5 0xA26A95`
- **Kane's Wrath** (`RetailExe\1.3\cnc3ep1.dat`): render `0xB7D4D0`, sites `0x5ADCEF 0x5C3C4E 0x5C40DF 0x81AC72 0xA06308 0xA06350 0xA07349`

The RA3 particle-simulation throttle doesn't find its hook in C&C3 (the particle manager is compiled differently), and neither game needs it: both run at 120 with no flicker and normal speed.

The game folder comes from Steam's `%command%` (the first `.exe` argument, e.g. `RA3.exe`, `CNC3.exe` or `CNC3EP1.exe`), and the real exe comes from the newest `*_1.N.SkuDef` in that folder.

## Soviet and Empire construction (RA3)

Soviet and Empire buildings looked finished the moment they were placed, while the real build time still applied. Putting the build on hold showed the right stage, which turned out to be the clue.

The build-up progress lives in `StructureUnpackUpdate` (RA3 1.13 `0x71FE90`). It converts the build's start tick and duration to client frames using the 30 fps convention (`tick / 15 * 1000 * framesPerMs`, with `framesPerMs` fixed at 0.03), then measures "now" with `GameClient::getFrame()`, the real drawn-frame count. At 120 fps "now" is four times bigger than the start stamp, so progress is clamped to 100% on the first frame. On hold it takes a different branch that uses logic frames only, hence the correct stage. Same kind of bug as the particle flicker: two clocks that only agree at 30 fps.

The fix replaces both `getFrame()` reads in that function with the current logic frame run through the exact same conversion, so both sides are in the same units. The rising model (W3D animation mode at `0x937DCF`, frame = progress x last frame) and the bar on the building both read this progress, so both are fixed. Found by a masked byte pattern: `0x71FED6` in 1.13 and `0x735116` in 1.12.

## Game speed above 90 fps (RA3)

RA3 splits every 15 Hz logic tick into 6 phases. Below 90 fps it batches several phases per frame. At 90 and above it runs one phase per frame, and after phase 6 it starts the next tick straight away, so a tick always takes 6 frames. That's exact at 90, but at 120 it gave 20 ticks/s (33% fast) and at 240 it gave 40. Measured by reading the logic frame counter over 20 seconds.

The per-frame engine update (`0x62B920`) is hooked at two spots. When frames per tick `R = fps/15` is above 6, phase `p` only runs once the tick has had `ceil(k*6/R)` frames (`k` = frames since the tick started), and a new tick starts only after `R` frames. At 120 that's phase 1, 2, 3, idle, 4, 5, 6, idle. Phase 1 still gets exactly one frame, so the network code that runs at tick boundaries is untouched. On every frame the tick-interpolation value that drawables use to blend between logic states (`[engine+60h]`) is set to `k/R`, so units move smoothly through the idle frames. At 90 and below the hook does nothing. Measured after the fix: 14.99 ticks/s at 119.9 fps.

The frame limiter also truncated its per-frame budget to whole milliseconds (`trunc(66.67 / R)`): 8 ms at 120 fps is really 125 fps, about 4% fast (same at 60 and 240; 90 and stock 30 are about 1% fast). It now carries the dropped fraction into the next frame (8, 8, 9, ...). The same fix is applied to Tiberium Wars and Kane's Wrath, where the limiter calls `_ftol` instead (that block was found by CNCStuff/cnc3_fps_patch).

## Camera scrolling (RA3)

Edge scrolling, arrow keys and right-drag scrolling each add a step every drawn frame, so at 120 fps the camera scrolled four times as fast. All of them end in the tactical view's `scrollBy(Coord2D*)` (vtable slot `0xC155B8` in 1.13, found by a byte pattern of the function and then its single vtable reference). That slot now points to a small wrapper that multiplies the delta by `30 / fps` and jumps to the original, so the scroll amount the view stores stays consistent too. Same idea as CNCStuff/cnc3_fps_patch's C&C3 camera fix.

## Fades (RA3)

Drawables that fade in or out (dying units, cloaking, some effect objects) get the fade length in client frames via `ms * framesPerMs` (30 fps units), but stamp the start and measure progress with `GameClient::getFrame()`, the real drawn-frame count. At 120 fps fades finished four times early. The five places that touch that clock (the fade setters at `0x543AC0`/`0x543B20`, two more that restamp `[obj+338h]`, and the per-frame update at `0x557F70`) now get `ceil(getFrame * 30 / fps)` instead, the same 30 Hz frame number the Anim2D fix uses.

## Known leftovers

- Particles simulate at 30 Hz (like stock), so smoke motion is a little less smooth than units. Try `--pfx off` if you want to experiment.
- A small UI pulse calculation reads the render rate in a form I didn't bother redirecting. I haven't seen it matter.
- Only tested on the current Steam builds (RA3 1.13, Tiberium Wars 1.10, Kane's Wrath 1.3).
