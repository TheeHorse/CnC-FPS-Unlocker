# How it works

Notes for anyone who wants to build on this. Addresses are for the current Steam `RA3_1.13.game` (EA's 2025 rebuild, PE timestamp `0x67B7A95B`). Everything gets found by byte pattern so small updates might still work, run it with `--check` to see.

## Basics

- Render rate is at `0xCB8514` (int, 30 by default), logic rate at `0xCB8510` (int, 15). Logic and render are already separate in this build, 2 render frames per logic tick at stock
- Game speed comes from the logic rate. Only change the render side and speed stays normal
- Changing the logic rate does speed the game up. I tried it, don't bother

## Why nothing on disk changes

Tacitus only accepts exes it knows, so a patched exe gets rejected. So instead the launcher:

1. starts the stock game with `CREATE_SUSPENDED` and the same `-config "<SkuDef>"` command line RA3.exe would use
2. allocates a bit of memory in the game, writes the fps there and repoints some `[0xCB8514]` reads at it
3. resumes the game and waits for it to close so Steam still shows you in-game

## Which reads get redirected (the important part)

The render rate gets read in 119 places, for two different things:

**1. Actual frame pacing.** These need the new fps:
- the frame limiter, `frame_ms = 1000 / fps` (`0xBBBC00`)
- frames per logic tick in the main loop, `fps / logic` (`0x63F4F6`), plus a matching check (`0x628C90`)
- a few static inits and object fields that pace frames (ms per frame as float, fps as float etc)

**2. "30ths of a second" as a time unit for effects.** These have to stay 30:
- a static init builds `framesPerMs = fps * 0.001` (`0xCE511C`) and the particle code (`0x6EA76A`) uses it to stamp particle birth times
- the particle manager's "now" is `clientTimeMs * fps * 0.001` (`0x6F6DF0`)
- the GPU particle shader gets its clock from the effect parameter `Time`, set at `0x4DA810` as `clientTimeMs / 1000`, and a preshader multiplies that by a fixed 30. So the shader always counts in 30ths

Redirect the group 2 reads and the CPU birth times run `fps/30` times ahead of the shader clock. The shader does `age = c38.x - birth` (like `296.88 - 1186.6`), gets a big negative age, and the size (`b^age`) and color keyframe math blow up into quads that cover the screen. That's the white/colored flicker every high fps attempt for this game seems to run into. Worst near water because the particles get drawn into the 512x512 water reflection and the water + bloom smear it everywhere.

Default is all the core pacing sites except the `framesPerMs` init (`--core 0-4,6,7`). The particle sim call (`0x61D2BD` -> `0x6FAF80`) also goes through a small accumulator stub so it still runs at 30hz like stock. Only the sim step gets throttled though, the draw bucket rebuild in the same function has to run every frame or you get stale pointers and a crash.

## How I found the flicker

Guessing got me nowhere. Tick rate, frame_ms conversions, 1/30 and 30.0 constants, particle step rate, none of it made a difference. What actually worked:

1. capture a campaign session with apitrace's `d3d9.dll` wrapper (`--trace G:\somewhere\ra3.trace` makes the launcher set `TRACE_FILE`)
2. replay with `d3dretrace -S "*/frame"` to dump every frame, then score frames that differ from both neighbours while the neighbours match each other. Turned out the flashes were periodic, about once per logic tick
3. snapshot every draw call (`-S "<range>/draw"`) in one bad frame and the good frame after it and diff them draw by draw. Identical up to draw #570, a 4 particle GPU batch drawn into the water reflection
4. everything bound for that draw was the same in both frames (shader, textures, render states, vertex data) except the shader clock `c38`. The birth times in the vertex data were exactly 4.00x the clock at 120 fps, which pointed right at the fps based time conversion

## Tiberium Wars and Kane's Wrath

Same design as RA3: render 30 and logic 15 next to each other, same frame limiter, same main loop, same static inits including the `framesPerMs = fps * 0.001` one. Compiled differently though, so exact byte patterns don't carry over. Since v1.1 the pacing reads get found by what they do:

| What | How it's found |
|---|---|
| frame limiter | `mov eax,1000 / xor edx,edx / div [fps]` |
| frames per logic tick | `mov eax,[fps] / xor edx,edx / div [logic]` then a compare with 6 (`cmp reg,6`, or `push 6 ... cmp`) |
| the 64000 one | `mov reg,[fps]` with `mov eax,64000` right after |
| static inits | `fild [fps]` then `fdivr` (1000/fps) or `fstp` (fps). If it's followed by `fmul` it's a time conversion, leave it |
| object field | the one `mov eax,[fps] / mov [esi+x],eax` in a constructor |

On RA3 that picks exactly the same 7 sites as the hand tuned version. Addresses:

- **Tiberium Wars** (`RetailExe\1.10\cnc3game.dat`): render `0xB877C4`, sites `0x548132 0x55D94F 0x55DDDF 0x865660 0xA24B9D 0xA24BE5 0xA26A95`
- **Kane's Wrath** (`RetailExe\1.3\cnc3ep1.dat`): render `0xB7D4D0`, sites `0x5ADCEF 0x5C3C4E 0x5C40DF 0x81AC72 0xA06308 0xA06350 0xA07349`

The RA3 particle throttle doesn't find its spot in C&C3 (particle manager is compiled differently) but neither game needs it, both run at 120 with no flicker.

The game folder comes from Steam's `%command%` (first `.exe` in it, so `RA3.exe`, `CNC3.exe` or `CNC3EP1.exe`) and the real exe comes from the newest `*_1.N.SkuDef` in that folder.

## Soviet and Empire construction (RA3)

Soviet and Empire buildings looked finished the second you placed them, but the real build time still applied. Putting the build on hold showed the right stage, which ended up being the clue.

Build progress is in `StructureUnpackUpdate` (1.13 `0x71FE90`). It converts the start tick and duration into client frames the 30 fps way (`tick / 15 * 1000 * framesPerMs`, with `framesPerMs` stuck at 0.03), then gets "now" from `GameClient::getFrame()`, which is the real drawn frame count. At 120 fps "now" is 4x bigger than the start stamp so progress clamps to 100% on the first frame. On hold it takes a different path that only uses logic frames, that's why hold looked right. Same kind of bug as the particle flicker, two clocks that only agree at 30 fps.

The fix swaps both `getFrame()` reads in there for the current logic frame put through the exact same conversion, so both sides are in the same units. The rising model (W3D anim mode at `0x937DCF`, frame = progress x last frame) and the bar on the building both read this progress so both get fixed. Found with a masked byte pattern: `0x71FED6` in 1.13, `0x735116` in 1.12.

To make it rise smoothly instead of stepping 15 times a second, the whole thing runs in 1/240 s units (the three conversions point at a copy of `framesPerMs` x 8) and "now" adds the engine's between-ticks fraction.

## Game speed (all three games)

All three games split each 15hz logic tick into 6 phases. Under 90 fps they batch a few phases per frame. At 90 and up it's one phase per frame and the next tick starts right after phase 6, so a tick always takes 6 drawn frames. That's exact at 90 if your PC holds it, but RA3 at 120 ran 20 ticks/s (33% fast), and any time a PC drew less than the target the game went slow-mo (TW on a laptop getting ~78 fps at a 120 target: 10.5 ticks/s). Measured by reading the logic frame counter (RA3) or counting phase wraps (C&C3) over 20 seconds.

The per-frame engine update (1.13 `0x62B920`, TW `0x54B0CE`) gets hooked in two spots and ticks are scheduled off the clock (`timeGetTime`, through the same helper the frame limiter uses) instead of by frame count:

- time is kept in 1/3 ms units so a tick is exactly 200 and nothing drifts
- a new tick starts once 66.67 ms have passed. If the network holds a tick back the engine reverts the phase, the hook sees that next frame and retries every frame like stock. More than 2 ticks behind it just resyncs instead of spiralling
- every frame it runs whatever phases are due by the clock (`1 + floor(t * 6 / 200)`, so phase k runs once (k-1)/6 of the tick has gone by, same spacing as stock). On a fast PC some frames run nothing, on a slow one the hook runs the extra phases itself through the engine's phase dispatcher (vtable `+90h` in RA3, `+94h` in C&C3) and lets the original code do the last one
- the interpolation value units blend with (`[engine+60h]` RA3, `+48h` C&C3) gets set every frame to (time since the frame this tick started on + this frame's length) / 200, max 1. So it goes up evenly to 1 by the last frame before the next tick (1/8 ... 8/8 at 120) with no frozen frames

This runs at every frame rate. Under 90 the stock engine batched phases itself (and ran a bit slow, KW at 60 was 14.65 ticks/s), so the two places that do `fps / logic` and compare with 6 (phase dispatcher and tick boundary check) read `max(fps, 90)` instead, which keeps them in one-phase-per-call mode and leaves the batching to the clock. After the fix: RA3 14.99 ticks/s at 120 and 15.00 at 60, TW 15.00 at 120 (same laptop that did 10.5 before), KW 15.00 at 120 and 60.

The frame limiter also cut its per-frame time down to whole ms (`trunc(66.67 / R)`): 8 ms at 120 is really 125 fps, about 4% fast (same at 60 and 240, 90 and stock 30 are about 1% fast). Now it carries the leftover into the next frame (8, 8, 9, ...). TW and KW get the same fix, their limiter calls `_ftol` instead (CNCStuff/cnc3_fps_patch found that block).

RA3 also only interpolated a unit for 6 drawn frames after its last logic update (`frame - [drawable+130h] >= 6` skips interpolation in `0x558EC0` and `0x553090`), which is all a tick has at 90. At 120 with 8 frames a tick every unit froze for the last 2 frames of each tick and then jumped, so movement looked like it was running at a lower frame rate. Both checks use `fps/15 + 2` now. TW and KW have the same two checks (`frame - [drawable+138h] < 6`, different registers in each game) and get the same fix. Early v1.6 builds had this problem plus an interpolation value that maxed out too early, so units hitched and sometimes looked stuck.

## Camera scrolling (all three games)

Edge scroll, arrow keys and right-drag all add a step every drawn frame, so at 120 the camera went 4x as fast. They all end up in the tactical view's `scrollBy(Coord2D*)` (vtable slot `0xC155B8` in 1.13, found by a byte pattern of the function and then its one vtable reference). That slot now points at a little wrapper that multiplies the delta by `30 / fps` and jumps to the original, so what the view stores stays consistent too. TW and KW get the same wrapper on their scrollBy (TW `0x82F635`, CNCStuff/cnc3_fps_patch had already found it).

## Fades (RA3)

Stuff that fades in or out (dying units, cloaking, some effects) gets its fade length in 30 fps frames via `ms * framesPerMs`, but stamps the start and checks progress with `GameClient::getFrame()`, the real drawn frame count. So at 120 fades finished 4x early. The 5 places that touch that clock (fade setters at `0x543AC0`/`0x543B20`, two more that restamp `[obj+338h]`, and the per-frame update at `0x557F70`) now get `ceil(getFrame * 30 / fps)`, same 30hz frame number the Anim2D fix uses.

## Camera zoom-out extra (RA3)

The view's `setZoom` (`0x616BE0` in 1.13) clamps zoom between two values from a small "camera limits" object (`[view+26FCh]`, min 350 and max 550 on stock maps). The call that gets the max (`0x616C8E`) goes through a wrapper that multiplies it by the `zoom` value from the ini, but only when there's no network object (`[0xCECF3C]` is null in skirmish and campaign, set online/LAN). Past about 1.75x you can see the edge of the terrain renderer's draw window at the top of the screen (ground stops in a straight line while objects still draw), so the setup goes up to 1.75x.

## Leftovers

- Particles simulate at 30hz like stock so smoke is a little less smooth than units. Try `--pfx off` if you want to mess with it
- There's a small UI pulse calculation that reads the render rate in a way I didn't bother redirecting. Haven't seen it matter
- Only tested on the current Steam builds (RA3 1.13, TW 1.10, KW 1.3)
