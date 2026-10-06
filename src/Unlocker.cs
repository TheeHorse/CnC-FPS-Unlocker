// SAGE Unlocked - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/SAGE-Unlocked
//
// CnCFpsUnlocker.dll: the actual fixes. d3d9.dll / dinput8.dll (dll/proxy.c) loads this inside the game
// right before it starts, it reads RA3HighFps.ini and patches the game's own memory. nothing on disk
// changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static class Program
{
    const uint ImageBase = 0x400000;

    // called from inside the game (dll/proxy.c)
    internal static int InProcess(string dir)
    {
        return Patch(IntPtr.Zero, Process.GetCurrentProcess().MainModule.FileName, dir, "drop-in DLL");
    }

    // RA3HighFps.exe on linux (proton's .net runs exes but the dll can't host it): game started suspended,
    // same patches from outside. dir = the game exe's folder, where the dll and ini are
    internal static int Launch(IntPtr proc, string exe, string dir)
    {
        return Patch(proc, exe, dir, "RA3HighFps.exe");
    }

    static int Patch(IntPtr proc, string exe, string dir, string how)
    {
        string ini = Path.Combine(dir, "RA3HighFps.ini");
        byte[] img = null;
        try
        {
            int fps = ReadIniFps(ini, 120);
            if (fps < 30 || fps > 240 || fps % 15 != 0) fps = 120;
            float zoom; if (!float.TryParse(ReadIni(ini, "zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoom) || zoom < 1f || zoom > 3f) zoom = 1f;
            zoom = Math.Min(zoom, 1.5f);   // further out the ground/water stops drawing at the top on hilly maps (#10)
            int hz = MonitorHz();
            if (hz >= 30 && fps > hz / 15 * 15) fps = Math.Max(30, hz / 15 * 15);
            img = MapImage(File.ReadAllBytes(exe));
            skip = (ReadIni(ini, "skip") ?? "").ToLowerInvariant();
            offscreenAnim = ReadIni(ini, "offscreenanim") == "1";
            // bfme2: same engine family, its own code shapes (cdq/idiv), so its own path
            BfmeSites bfme = Scan(img, FrameMsSig).Count == 0 ? FindBfme(img) : null;
            if (bfme != null)
            {
                if (BitConverter.ToUInt32(Read(proc, bfme.Fps[0], 4), 0) != BitConverter.ToUInt32(img, (int)(bfme.Fps[0] - ImageBase)))
                    return 2;
                ApplyPatchesBfme(proc, img, fps, bfme);
                WriteLog(dir, DateTime.Now + "  " + how + ", fps=" + fps + (skip != "" ? ", skip=" + skip : "") + ", exe=" + Path.GetFileName(exe) +
                    " (bfme2)\r\n" + found + "\r\n" + patched + "\r\n");
                return 1;
            }
            // already patched (old v1.6 launcher still set as the steam launch option, or RA3HighFps.exe got there first)
            uint render = FindRenderFps(img);
            List<uint> pacing = FindPacingSites(img, render, render - 4);
            if (pacing.Count > 0 && BitConverter.ToUInt32(Read(proc, pacing[0], 4), 0) != BitConverter.ToUInt32(img, (int)(pacing[0] - ImageBase)))
                return 2;
            ApplyPatches(proc, img, fps, zoom, true, null, false);
            WriteLog(dir, DateTime.Now + "  " + how + ", fps=" + fps + ", zoom=" + zoom +
                (skip != "" ? ", skip=" + skip : "") + ", exe=" + Path.GetFileName(exe) + "\r\n" + found + "\r\n" + patched + "\r\n");
            return 1;
        }
        catch (Exception e)
        {
            string report = "";
            try { if (img != null) report = BuildReport(img, exe); } catch (Exception re) { report = "report failed: " + re.Message; }
            WriteLog(dir, DateTime.Now + "  " + how + " failed: " + e + "\r\n\r\n" + report);
            return 0;
        }
    }

    // %TEMP%\RA3HighFps.log, and a copy next to the game where people look first (may be read-only, then just temp).
    // the drop-in dll appends crash lines to both
    static void WriteLog(string dir, string text)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), text); } catch { }
        try { File.WriteAllText(Path.Combine(dir, "RA3HighFps.log"), text); } catch { }
    }

    // for unknown builds: exe info, sections, and the bytes around every render/logic fps read
    static string BuildReport(byte[] img, string exe)
    {
        var sb = new StringBuilder();
        int pe = BitConverter.ToInt32(img, 0x3C), n = BitConverter.ToUInt16(img, pe + 6), opt = pe + 24;
        int secs = opt + BitConverter.ToUInt16(img, pe + 20);
        sb.AppendFormat("exe {0}\r\nsize {1}  timestamp 0x{2:X8}  entry 0x{3:X}\r\n", exe, new FileInfo(exe).Length,
            BitConverter.ToUInt32(img, pe + 8), BitConverter.ToUInt32(img, opt + 16));
        for (int i = 0; i < n; i++)
        {
            int s = secs + i * 40;
            sb.AppendFormat("section {0,-8} va 0x{1:X} vsize 0x{2:X} raw 0x{3:X} rawsize 0x{4:X}\r\n",
                Encoding.ASCII.GetString(img, s, 8).TrimEnd('\0'), BitConverter.ToUInt32(img, s + 12),
                BitConverter.ToUInt32(img, s + 8), BitConverter.ToUInt32(img, s + 20), BitConverter.ToUInt32(img, s + 16));
        }
        var hits = Scan(img, FrameMsSig);
        sb.AppendFormat("limiter matches: {0}\r\n", hits.Count);
        if (hits.Count != 1) return sb.ToString();
        uint r = BitConverter.ToUInt32(img, hits[0] + 9), l = r - 4;
        int ro = (int)(r - ImageBase);
        sb.AppendFormat("render @0x{0:X} (file value {1}), logic @0x{2:X}\r\n",
            r, ro >= 0 && ro + 4 <= img.Length ? BitConverter.ToInt32(img, ro).ToString() : "?", l);
        int end = TextEnd(img);
        foreach (uint target in new[] { r, l })
        {
            byte[] t = BitConverter.GetBytes(target);
            int count = 0;
            for (int i = 0x1010; i < end - 32 && count < 300; i++)
            {
                if (img[i] != t[0] || img[i + 1] != t[1] || img[i + 2] != t[2] || img[i + 3] != t[3]) continue;
                sb.AppendFormat("{0} 0x{1:X}: {2} | {3}\r\n", target == r ? "R" : "L", ImageBase + (uint)i,
                    Hex(img, i - 12, 12), Hex(img, i, 28));
                count++;
            }
        }
        return sb.ToString();
    }

    // ini skip=fades,interp,... turns single fixes off (for tracking down problems with mods)
    static string skip = "", found = "", patched = "";
    static bool offscreenAnim = false;   // ini offscreenanim=1: #13 experiment, off by default (didn't make off-screen shadows smooth)
    static bool On(string name) { return !skip.Split(',').Select(s => s.Trim()).Contains(name); }

    // proc isn't used, it's always our own process (kept so the patch functions read the same as before)
    static List<uint> ApplyPatches(IntPtr proc, byte[] img, int fps, float zoom, bool throttlePfx, string extra, bool ticks)
    {
        uint render = FindRenderFps(img);
        List<uint> sites = FindPacingSites(img, render, render - 4);
        sites.AddRange(SelectSites(FindExtraSites(img, render, render - 4, sites), extra));
        if (ticks) sites.AddRange(FindTickStores(img, render, render - 4));
        uint pfxSim, pfxSite = FindParticleSim(img, out pfxSim);
        uint modelStep = FindModelTransitionStep(img);
        List<uint> anim2d = FindAnim2DFrameReads(img);
        uint unpack = FindUnpackProgress(img);
        bool limiterRA3;
        uint limiter = FindLimiterRounding(img, out limiterRA3);
        uint scrollFunc, scrollSlot = FindScrollBySlot(img, out scrollFunc);
        List<uint> fades = FindFadeFrameReads(img);
        uint netObject, zoomSite = FindZoomSite(img, out netObject);
        List<uint> interpWindow = FindInterpWindow(img);
        SchedSite schedSite = FindScheduler(img);
        bool sched = schedSite != null && On("sched");   // skip=sched falls back to the plain fps redirect
        HeldCamera held = FindHeldCamera(img);
        Cnc3Fx fx = FindCnc3Fx(img, modelStep);
        uint trailLock = FindTrailLock(img);
        SwaySite sway = FindSway(img);
        uint stream = FindStreamUpdate(img);
        List<uint> fpsFrames = FindFpsFrameReads(img, render, sites);
        Blinks blinks = FindBlinks(img);
        PulseSite pulse = FindPulse(img);
        TurretSite turretSite = FindTurretInterp(img);
        ToppleSite topple = FindTopple(img);
        uint animGate = FindAnimGate(img);
        if (pfxSite == 0 && fx.PfxSite != 0) { pfxSite = fx.PfxSite; pfxSim = fx.PfxSim; }   // tw / kw
        // for the log: which fixes this exe has, so reports from unknown builds say what's missing
        var have = new[] {
            new { n = "sched", ok = schedSite != null }, new { n = "scroll", ok = scrollSlot != 0 }, new { n = "camerakeys", ok = held != null },
            new { n = "interp", ok = interpWindow.Count > 0 }, new { n = "limiter", ok = limiter != 0 }, new { n = "construction", ok = unpack != 0 },
            new { n = "anim2d", ok = anim2d.Count > 0 }, new { n = "models", ok = modelStep != 0 }, new { n = "particles", ok = pfxSite != 0 },
            new { n = "fades", ok = fades.Count > 0 || fx.Fades.Count > 0 }, new { n = "zoom", ok = zoomSite != 0 },
            new { n = "camsteps", ok = fx.CameraStep != 0 }, new { n = "fxframes", ok = fx.Frame5.Count > 0 }, new { n = "throb", ok = fx.Throb != 0 },
            new { n = "shake", ok = fx.Shake != 0 }, new { n = "traillock", ok = trailLock != 0 }, new { n = "sway", ok = sway != null }, new { n = "stream", ok = stream != 0 }, new { n = "fpsframes", ok = fpsFrames.Count > 0 }, new { n = "blinks", ok = blinks.Sites.Count > 0 }, new { n = "tint", ok = blinks.Tint.Count > 0 }, new { n = "modeltimer", ok = blinks.TimerUpdate != 0 }, new { n = "pulse", ok = pulse != null }, new { n = "turrets", ok = turretSite != null }, new { n = "topple", ok = topple != null }, new { n = "offscreenanim", ok = animGate != 0 } };
        found = "found: " + string.Join(" ", have.Where(h => h.ok).Select(h => h.n)) + " | missing: " + string.Join(" ", have.Where(h => !h.ok).Select(h => h.n));

        // +0 fps, +8 particle accum, +40 stubs
        IntPtr mem = Alloc(proc, 4096);
        if (mem == IntPtr.Zero) throw new Exception("couldn't allocate patch memory");
        Write(proc, (uint)mem, BitConverter.GetBytes(fps));
        if (sched)
        {
            // scheduler does the timing now, these two need the one-phase-per-call path (fps/15 >= 6)
            List<uint> ratio = sites.Where(s => IsPhaseRatioSite(img, s)).ToList();
            Write(proc, (uint)mem + 0x90, BitConverter.GetBytes(Math.Max(fps, 90)));
            Redirect(proc, ratio, (uint)mem + 0x90);
            Redirect(proc, sites.Except(ratio).ToList(), (uint)mem);
        }
        else Redirect(proc, sites, (uint)mem);
        if (On("zoom") && zoom > 1f && zoomSite != 0) PatchZoom(proc, zoomSite, netObject, zoom, (uint)mem + 0x98, (uint)mem + 0x4A0);
        if (On("fades") && fades.Count > 0) PatchFadeFrameReads(proc, img, fades, (uint)mem, (uint)mem + 0x440);
        if (On("scroll") && scrollSlot != 0 && fps > 30) PatchScrollBy(proc, scrollSlot, scrollFunc, fps, (uint)mem + 0x4F0, (uint)mem + 0x4F8, (uint)mem + 0x3C0);
        if (On("interp") && interpWindow.Count > 0) PatchInterpWindow(proc, interpWindow, fps);
        if (On("camerakeys") && held != null && fps > 30) PatchHeldCamera(proc, img, held, fps, (uint)mem + 0xC00, (uint)mem + 0xC40);
        if (sched) PatchScheduler(proc, img, schedSite, (uint)mem, (uint)mem);
        if (On("limiter") && limiter != 0) PatchLimiterRounding(proc, limiter, limiterRA3, (uint)mem + 0x28, (uint)mem + 0x2C, (uint)mem + 0x280);
        if (On("construction") && unpack != 0) PatchUnpack(proc, img, unpack, (uint)mem + 0x200, sched ? (uint)mem + 0x3C : 0, (uint)mem + 0x4E0);   // construction
        if (On("anim2d") && anim2d.Count > 0) PatchAnim2D(proc, img, anim2d, (uint)mem, (uint)mem + 0x100);
        Write(proc, (uint)mem + 0x10, BitConverter.GetBytes(1f / fps));   // 1/fps: model, camera and laser steps
        PatchCnc3Fx(proc, fx, fps, (uint)mem, (uint)mem + 0x10, (uint)mem + 0xD00, (uint)mem + 0xD40);
        if (On("traillock") && trailLock != 0) PatchTrailLock(proc, trailLock, (uint)mem + 0xE00);
        if (On("sway") && sway != null && fps > 30) PatchSway(proc, img, sway, (uint)mem, (uint)mem + 0xE40);
        if (On("fpsframes") && fpsFrames.Count > 0 && fps > 30) Redirect(proc, fpsFrames, (uint)mem);
        if (On("pulse") && pulse != null && fps > 30) PatchPulse(proc, pulse, (uint)mem, (uint)mem + 0xB20);
        if (On("topple") && topple != null && fps > 30) PatchTopple(proc, topple, (uint)mem + 0xBA0);
        if (On("offscreenanim") && offscreenAnim && animGate != 0 && fps > 30) Write(proc, animGate, new byte[] { 0xEB });   // jne -> jmp
        if (On("turrets") && turretSite != null && sched && fps > 30) PatchTurretInterp(proc, img, turretSite, schedSite, (uint)mem + 0xB80, (uint)mem + 0xD48);
        if ((blinks.Sites.Count > 0 || blinks.Tint.Count > 0 || blinks.TimerUpdate != 0) && fps > 30) PatchBlinks(proc, blinks, (uint)mem, (uint)mem + 0xB00);
        if (On("stream") && stream != 0 && fps > 30) PatchStreamUpdate(proc, img, stream, (uint)mem, (uint)mem + 0xFA0);
        if (On("models") && modelStep != 0) Redirect(proc, new List<uint> { modelStep }, (uint)mem + 0x10);   // 1/fps instead of 1/30
        if (On("particles") && throttlePfx && pfxSite != 0)
            ThrottleParticles(proc, pfxSite, pfxSim, (uint)mem, (uint)mem + 8, (uint)mem + 0x40);
        FlushCode(proc);

        // where everything went, so a crash address in the log can be matched to a fix
        Func<IEnumerable<uint>, string> hex = l => string.Join(" ", l.Where(a => a != 0).Select(a => "0x" + a.ToString("X")));
        patched = "patch memory 0x" + ((uint)mem).ToString("X") + "-0x" + ((uint)mem + 0xFFF).ToString("X") +
            "\r\nsites: fps " + hex(sites) + " | fades " + hex(fades) + " | anim2d " + hex(anim2d) + " | construction " + hex(new[] { unpack }) +
            " | limiter " + hex(new[] { limiter }) + " | scroll " + hex(new[] { scrollSlot }) + " | zoom " + hex(new[] { zoomSite }) +
            " | interp " + hex(interpWindow) + " | models " + hex(new[] { modelStep }) + " | particles " + hex(new[] { pfxSite }) +
            (schedSite != null ? " | sched " + hex(new[] { schedSite.Advance, schedSite.Exit }) : "") +
            (held != null ? " | camerakeys " + hex(new[] { held.ZoomIn, held.ZoomOut, held.Rotate }) : "") +
            (trailLock != 0 ? " | traillock " + hex(new[] { trailLock }) : "") +
            (sway != null ? " | sway " + hex(new[] { sway.Guard }) : "") +
            (stream != 0 ? " | stream " + hex(new[] { stream }) : "") +
            (fpsFrames.Count > 0 ? " | fpsframes " + hex(fpsFrames) : "") +
            (blinks.Sites.Count > 0 ? " | blinks " + hex(blinks.Sites) : "") +
            (blinks.Tint.Count > 0 ? " | tint " + hex(blinks.Tint) : "") +
            (blinks.TimerUpdate != 0 ? " | modeltimer " + hex(new[] { blinks.TimerInit, blinks.TimerUpdate }) : "") +
            (pulse != null ? " | pulse " + hex(new[] { pulse.Call, pulse.Sine }) : "") +
            (turretSite != null ? " | turrets " + hex(new[] { turretSite.Site }) : "") +
            (topple != null ? " | topple " + hex(new[] { topple.Hook }) : "") +
            (animGate != 0 ? " | offscreenanim " + hex(new[] { animGate }) : "") +
            " | fx " + hex(fx.Frame5.Concat(new[] { fx.TracerUpdate, fx.CameraStep, fx.LaserStep, fx.Throb, fx.Shake })) +
            (fx.Fades.Count > 0 ? " | cnc3 fades " + hex(fx.Fades.Concat(new[] { fx.PulseSet, fx.PulseUpdate })) : "");
        return sites;
    }


    static string Hex(byte[] b, int off, int n)
    {
        if (b == null) return "(unreadable)";
        return string.Join(" ", b.Skip(off).Take(n).Select(x => x.ToString("X2")));
    }

    static void Redirect(IntPtr proc, List<uint> operands, uint target)
    {
        byte[] addr = BitConverter.GetBytes(target);
        foreach (uint va in operands)
        {
            uint old;
            if (!Protect(proc, (IntPtr)va, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, va, addr);
            Protect(proc, (IntPtr)va, (UIntPtr)4, old, out old);
        }
    }


    static int MonitorHz()
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
        return EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 60;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    static int ReadIniFps(string ini, int fallback)
    {
        string v = ReadIni(ini, "fps");
        int n;
        return v != null && int.TryParse(v, out n) ? n : fallback;
    }

    static string ReadIni(string ini, string key)
    {
        if (!File.Exists(ini)) return null;
        foreach (string line in File.ReadAllLines(ini))
        {
            var m = Regex.Match(line, @"^\s*" + key + @"\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length > 0) return m.Groups[1].Value;
        }
        return null;
    }


    // ---- signatures ----
    // -1 = wildcard, R/L = render_fps / logic_fps addresses

    // mov eax,1000 / xor edx,edx / div [render_fps] / mov [frame_ms],eax
    static readonly int[] FrameMsSig = { 0xB8, 0xE8, 0x03, 0x00, 0x00, 0x33, 0xD2, 0xF7, 0x35, -1, -1, -1, -1, 0xA3 };

    static uint FindRenderFps(byte[] img)
    {
        var hits = Scan(img, FrameMsSig);
        if (hits.Count != 1) throw new Exception("Unsupported game build (frame limiter not found).");
        return BitConverter.ToUInt32(img, hits[0] + 9);
    }

    // reads of render_fps that do frame pacing. same rules for ra3 and cnc3.
    // leaves the fps*0.001 framesPerMs init alone, particles use it as a 30hz clock
    static List<uint> FindPacingSites(byte[] img, uint r, uint l)
    {
        int end = TextEnd(img);
        byte[] R = BitConverter.GetBytes(r), L = BitConverter.GetBytes(l);
        Func<int, byte[], bool> at = (i, b) => img[i] == b[0] && img[i + 1] == b[1] && img[i + 2] == b[2] && img[i + 3] == b[3];
        var sites = new List<uint>();
        int tick = 0, ms64k = 0, objRate = -1, objHits = 0;
        // right after the div [logic]: cmp reg,6 or push 6 ... cmp reg,reg
        Func<int, bool> cmp6 = s =>
        {
            bool push6 = false;
            for (int k = s; k < s + 12; k++)
            {
                if (img[k] == 0x83 && img[k + 1] >= 0xF8 && img[k + 2] == 0x06) return true;
                if (img[k] == 0x6A && img[k + 1] == 0x06) push6 = true;
                if (push6 && img[k] == 0x3B) return true;
            }
            return false;
        };

        for (int i = 0x1002; i < end - 32; i++)
        {
            if (!at(i, R)) continue;
            uint va = ImageBase + (uint)i;

            // limiter: mov eax,1000 / xor edx,edx / div [fps]
            if (img[i - 9] == 0xB8 && img[i - 8] == 0xE8 && img[i - 7] == 0x03 && img[i - 2] == 0xF7 && img[i - 1] == 0x35)
            { sites.Add(va); continue; }

            // frames per tick: mov eax,[fps] / xor edx,edx / div [logic], then cmp with 6
            if (img[i - 1] == 0xA1 && img[i + 4] == 0x33 && img[i + 5] == 0xD2 && img[i + 6] == 0xF7 && img[i + 7] == 0x35 && at(i + 8, L))
            {
                if (cmp6(i + 12)) { sites.Add(va); tick++; }
                continue;
            }

            // same thing split up (KW 1.02 retail 2008): mov eax,[fps] / jmp body, body = xor edx,edx / div [logic] ...
            if (img[i - 1] == 0xA1 && img[i + 4] == 0xE9)
            {
                long t = i + 9L + BitConverter.ToInt32(img, i + 5);
                if (t > 0x1000 && t < end - 32 && img[t] == 0x33 && img[t + 1] == 0xD2 && img[t + 2] == 0xF7 && img[t + 3] == 0x35
                    && at((int)t + 4, L) && cmp6((int)t + 8))
                { sites.Add(va); tick++; }
                continue;
            }

            // mov reg,[fps] ... mov eax,64000
            if (img[i - 2] == 0x8B && (img[i - 1] & 0xC7) == 0x05)
            {
                for (int k = i + 4; k < i + 24; k++)
                    if (img[k] == 0xB8 && img[k + 1] == 0x00 && img[k + 2] == 0xFA && img[k + 3] == 0 && img[k + 4] == 0)
                    { sites.Add(va); ms64k++; break; }
                continue;
            }

            // static init: push ecx / mov eax,[fps] / fild [fps] / ... / op
            // take 1000/fps (fdivr) and fps (fstp), skip fmul ones (time units)
            if (img[i - 2] == 0x51 && img[i - 1] == 0xA1 && img[i + 4] == 0xDB && img[i + 5] == 0x05 && at(i + 6, R)
                && img[i + 10] == 0x85 && img[i + 12] == 0x7D && img[i + 14] == 0xD8 && img[i + 15] == 0x05)
            {
                byte op1 = img[i + 20], op2 = img[i + 21];
                if ((op1 == 0xD8 && op2 == 0x3D) || (op1 == 0xD9 && op2 == 0x1D)) sites.Add(va + 6);
                continue;
            }

            // ctor: mov eax,[fps] / mov [esi+x],eax
            if (img[i - 1] == 0xA1 && img[i + 4] == 0x89 && img[i + 5] == 0x86 && img[i + 8] == 0 && img[i + 9] == 0)
            { objRate = i; objHits++; }
        }
        if (objHits == 1) sites.Add(ImageBase + (uint)objRate);

        if (!sites.Any(s => BitConverter.ToUInt32(img, (int)(s - ImageBase)) == r && img[(int)(s - ImageBase) - 9] == 0xB8))
            throw new Exception("Unsupported game build (frame limiter not found).");
        if (tick < 2)
            throw new Exception("Unsupported game build (main loop not found).");
        sites.Sort();
        return sites;
    }

    // all the other render_fps reads (mostly fild in visual code), minus stuff that has to stay 30:
    // fps/2, fps/logic, particle clock (call getTime / imul [fps]), time unit inits
    static List<uint> FindExtraSites(byte[] img, uint r, uint l, List<uint> pacing)
    {
        int end = TextEnd(img);
        byte[] R = BitConverter.GetBytes(r), L = BitConverter.GetBytes(l);
        Func<int, byte[], bool> at = (i, b) => img[i] == b[0] && img[i + 1] == b[1] && img[i + 2] == b[2] && img[i + 3] == b[3];
        var twoByte = new[] { "8B05", "8B0D", "8B15", "8B1D", "8B35", "8B3D", "DB05", "0FAF05", "0FAF0D", "0FAF15", "3B05", "3B0D", "3B15" };
        var list = new List<uint>();
        for (int i = 0x1003; i < end - 32; i++)
        {
            if (!at(i, R)) continue;
            uint va = ImageBase + (uint)i;
            if (pacing.Contains(va) || pacing.Contains(va - 6) || pacing.Contains(va + 6)) continue;
            string p2 = img[i - 2].ToString("X2") + img[i - 1].ToString("X2");
            string p3 = img[i - 3].ToString("X2") + p2;
            if (!(img[i - 1] == 0xA1 || twoByte.Contains(p2) || twoByte.Contains(p3))) continue;
            if (img[i + 4] == 0xD1 && img[i + 5] >= 0xE8 && img[i + 5] <= 0xEF) continue;          // fps/2 = logic rate
            bool divL = false;
            for (int k = i + 4; k < i + 10; k++)
                if (img[k] == 0xF7 && (img[k + 1] == 0x35 || img[k + 1] == 0x3D) && at(k + 2, L)) divL = true;
            if (divL) continue;                                                                    // fps / logic
            if (p3.StartsWith("0FAF") && img[i - 8] == 0xE8) continue;                             // call getTime / imul [fps]
            // time unit init (... fmul)
            int s = img[i - 1] == 0xA1 ? i : (p2 == "DB05" ? i - 6 : -1);
            if (s > 0 && img[s - 2] == 0x51 && img[s - 1] == 0xA1 && img[s + 4] == 0xDB && img[s + 14] == 0xD8
                && img[s + 20] == 0xD8 && img[s + 21] == 0x0D) continue;
            list.Add(va);
        }
        return list;
    }

    // cached copies of frames-per-tick (fps/logic stored into an object). experimental, ticks=on
    static List<uint> FindTickStores(byte[] img, uint r, uint l)
    {
        int end = TextEnd(img);
        byte[] R = BitConverter.GetBytes(r), L = BitConverter.GetBytes(l);
        Func<int, byte[], bool> at = (i, b) => img[i] == b[0] && img[i + 1] == b[1] && img[i + 2] == b[2] && img[i + 3] == b[3];
        var list = new List<uint>();
        for (int i = 0x1001; i < end - 32; i++)
        {
            if (img[i - 1] != 0xA1 || !at(i, R) || img[i + 4] != 0x33 || img[i + 5] != 0xD2 || img[i + 6] != 0xF7 || img[i + 7] != 0x35 || !at(i + 8, L)) continue;
            for (int k = i + 12; k < i + 22; k++)
                if (img[k] == 0x89 && (img[k + 1] & 0xC0) == 0x40 && (img[k + 1] & 0x38) == 0x00)   // mov [reg+disp8],eax
                { list.Add(ImageBase + (uint)i); break; }
        }
        return list;
    }

    // "all", "none" or "0-20,25"
    static List<uint> SelectSites(List<uint> all, string spec)
    {
        if (spec == null || spec == "none") return new List<uint>();
        if (spec == "all") return all;
        var pick = new List<uint>();
        foreach (string part in spec.Split(','))
        {
            string[] ab = part.Split('-');
            int a = int.Parse(ab[0]), b = ab.Length > 1 ? int.Parse(ab[1]) : a;
            for (int k = a; k <= b && k < all.Count; k++) pick.Add(all[k]);
        }
        return pick;
    }

    // model transitions add 1/30 per drawn frame (addss xmm0,[1/30f]), 4x too fast at 120.
    // only this read gets moved, the constant is shared (pathfinding uses it too)
    // found by CNCStuff/cnc3_fps_patch
    static uint FindModelTransitionStep(byte[] img)
    {
        int end = TextEnd(img);
        uint hit = 0; int count = 0;
        for (int i = 0x1010; i < end - 8; i++)
        {
            if (img[i] != 0xF3 || img[i + 1] != 0x0F || img[i + 2] != 0x58 || img[i + 3] != 0x05) continue;   // addss xmm0,[m32]
            uint addr = BitConverter.ToUInt32(img, i + 4);
            int off = (int)(addr - ImageBase);
            if (off < 0 || off + 4 > img.Length || BitConverter.ToUInt32(img, off) != 0x3D088889) continue;    // 1/30f
            if (img[i - 8] != 0xF3 || img[i - 7] != 0x0F || img[i - 6] != 0x10) continue;                        // movss xmm0,[reg+disp32]
            if (!(img[i - 10] == 0xFF && img[i - 9] == 0xD0) && !(img[i - 11] == 0xFF && img[i - 10] == 0x50)) continue;  // call eax / call [eax+x]
            hit = ImageBase + (uint)i + 4; count++;
        }
        return count == 1 ? hit : 0;
    }

    // 2d sprite anims count drawn frames (made for 30fps), give them a 30hz frame number instead.
    // found by CNCStuff/cnc3_fps_patch
    static List<uint> FindAnim2DFrameReads(byte[] img)
    {
        int end = TextEnd(img);
        var list = new List<uint>();
        for (int i = 0x1000; i < end - 16; i++)
        {
            // mov r,[r+74h] / (add esp,x) / call r
            if (img[i] != 0x8B || (img[i + 1] & 0xC0) != 0x40 || img[i + 2] != 0x74) continue;
            int dst = (img[i + 1] >> 3) & 7;
            int c = i + 3;
            if (img[c] == 0x83 && img[c + 1] == 0xC4) c += 3;
            if (img[c] != 0xFF || img[c + 1] != (0xD0 | dst)) continue;
            int n = c + 2;
            bool set = img[n] == 0x89 && img[n + 1] == 0x46 && img[n + 2] == 0x08;                                  // mov [esi+8],eax
            bool upd = img[n] == 0x2B && img[n + 1] == 0x46 && img[n + 2] == 0x08 && img[n + 3] == 0x3B && img[n + 4] == 0x46 && img[n + 5] == 0x18;
            if (!set && !upd) continue;
            // the stub calls getFrame on ecx itself, so it has to be mov ecx,[client] / mov r,[ecx] / mov r,[r+74h]
            // (an unknown build with different code here gets no anim2d fix instead of a crash)
            bool fromClient = img[i - 8] == 0x8B && img[i - 7] == 0x0D && img[i - 2] == 0x8B && (img[i - 1] & 0xC7) == 0x01
                              && ((img[i - 1] >> 3) & 7) == (img[i + 1] & 7);
            if (!fromClient) return new List<uint>();
            list.Add(ImageBase + (uint)i);
        }
        // and the same client object at every site
        if (list.Select(va => BitConverter.ToUInt32(img, (int)(va - ImageBase) - 6)).Distinct().Count() > 1) return new List<uint>();
        return list;
    }

    // eax = ceil(getFrame() * 30 / fps)
    static void PatchAnim2D(IntPtr proc, byte[] img, List<uint> sites, uint fpsVa, uint stubVa)
    {
        var s = new List<byte>();
        Func<uint, byte[]> u = BitConverter.GetBytes;
        s.AddRange(new byte[] { 0x8B, 0x01, 0xFF, 0x50, 0x74 });              // mov eax,[ecx] / call [eax+74h]
        s.AddRange(new byte[] { 0x6B, 0xC0, 0x1E });                          // imul eax,eax,30
        s.AddRange(new byte[] { 0x8B, 0x15 }); s.AddRange(u(fpsVa));          // mov edx,[fps]
        s.AddRange(new byte[] { 0x8D, 0x44, 0x10, 0xFF });                    // lea eax,[eax+edx-1]
        s.AddRange(new byte[] { 0x33, 0xD2 });                                // xor edx,edx
        s.AddRange(new byte[] { 0xF7, 0x35 }); s.AddRange(u(fpsVa));          // div dword [fps]
        s.Add(0xC3);                                                          // ret
        Write(proc, stubVa, s.ToArray());
        foreach (uint site in sites)
        {
            // keep the add esp if there is one
            int o = (int)(site - ImageBase);
            var code = new List<byte>();
            if (img[o + 3] == 0x83 && img[o + 4] == 0xC4) code.AddRange(new byte[] { 0x83, 0xC4, img[o + 5] });
            code.Add(0xE8);
            code.AddRange(u(stubVa - (site + (uint)code.Count + 4)));
            uint old;
            if (!Protect(proc, (IntPtr)site, (UIntPtr)code.Count, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, site, code.ToArray());
            Protect(proc, (IntPtr)site, (UIntPtr)code.Count, old, out old);
        }
    }

    // limiter truncates to whole ms: 120fps -> 8ms = 125fps, ~4% fast. carry the leftover
    // into the next frame (8,8,9..) so it averages out. ra3 = inline fistp, cnc3 = _ftol
    // (cnc3 block found by CNCStuff/cnc3_fps_patch)
    const string LimiterPatternRA3 = "D8 3D ?? ?? ?? ?? 2B 05 ?? ?? ?? ?? A3 ?? ?? ?? ?? D9 6C 24 14 DF 7C 24 14 8B 74 24 14 3B C6";
    const string LimiterPatternCnc3 = "3B C8 76 07 C6 05 ?? ?? ?? ?? 00 80 3D ?? ?? ?? ?? 00 74 69 E8 ?? ?? ?? ?? DB 86 64 01 00 00 8B D8 D8 0D ?? ?? ?? ?? D8 3D ?? ?? ?? ?? E8";

    static uint FindUnique(byte[] img, string pattern)
    {
        int[] pat = pattern.Split(' ').Select(t => t == "??" ? -1 : Convert.ToInt32(t, 16)).ToArray();
        int end = TextEnd(img), found = -1;
        for (int i = 0x1000; i < end - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && (pat[j] < 0 || img[i + j] == pat[j])) j++;
            if (j < pat.Length) continue;
            if (found >= 0) return 0;
            found = i;
        }
        return found < 0 ? 0 : ImageBase + (uint)found;
    }

    static uint FindLimiterRounding(byte[] img, out bool ra3Style)
    {
        uint m = FindUnique(img, LimiterPatternRA3);
        ra3Style = m != 0;
        if (m != 0) return m + 0x15;
        m = FindUnique(img, LimiterPatternCnc3);
        return m != 0 ? m + 45 : 0;
    }

    static void PatchLimiterRounding(IntPtr proc, uint site, bool ra3Style, uint accVa, uint tmpVa, uint stubVa)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        var s = new List<byte> { 0xD8, 0x05 }; s.AddRange(u(accVa));               // fadd [acc]
        if (ra3Style)
        {
            // already in truncate mode here
            s.AddRange(new byte[] { 0xD9, 0xC0, 0xDB, 0x1D }); s.AddRange(u(tmpVa)); // fld st0 / fistp [tmp]
        }
        else
        {
            s.AddRange(new byte[] { 0x83, 0xEC, 0x04, 0xD9, 0x3C, 0x24, 0x66, 0x8B, 0x04, 0x24, 0x66, 0x0D, 0x00, 0x0C,
                                    0x66, 0x89, 0x44, 0x24, 0x02, 0xD9, 0x6C, 0x24, 0x02 });  // set truncate mode
            s.AddRange(new byte[] { 0xD9, 0xC0, 0xDB, 0x1D }); s.AddRange(u(tmpVa)); // fld st0 / fistp [tmp]
            s.AddRange(new byte[] { 0xD9, 0x2C, 0x24, 0x83, 0xC4, 0x04 });          // restore mode
        }
        s.AddRange(new byte[] { 0xDA, 0x25 }); s.AddRange(u(tmpVa));               // fisub [tmp]
        s.AddRange(new byte[] { 0xD9, 0x1D }); s.AddRange(u(accVa));               // fstp [acc]
        if (ra3Style) { s.AddRange(new byte[] { 0x8B, 0x35 }); s.AddRange(u(tmpVa)); }   // mov esi,[tmp]
        else { s.Add(0xA1); s.AddRange(u(tmpVa)); }                                        // mov eax,[tmp]
        s.Add(0xC3);
        Write(proc, stubVa, s.ToArray());

        var p = new List<byte> { 0xE8 }; p.AddRange(u(stubVa - (site + 5)));
        if (ra3Style) p.AddRange(new byte[] { 0x90, 0x90, 0x90 });
        uint old;
        if (!Protect(proc, (IntPtr)site, (UIntPtr)p.Count, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("couldn't unprotect game memory");
        Write(proc, site, p.ToArray());
        Protect(proc, (IntPtr)site, (UIntPtr)p.Count, old, out old);
    }

    // fades (dying units, stealth etc) time themselves with drawn frames -> 4x fast at 120.
    // same 30hz frame number trick as anim2d, 5 reads
    static readonly string[] FadeFramePatterns = {
        "8B 0D ?? ?? ?? ?? 8B 01 8B 50 74 FF D2 89 86 38 03 00 00",
        "8B 0D ?? ?? ?? ?? 8B 11 8B 42 74 FF D0 89 86 38 03 00 00",
        "8B 0D ?? ?? ?? ?? 8B 01 8B 50 74 FF D2 8B 96 FC 01 00 00 8B C8 2B 8E 38 03 00 00" };

    static List<uint> FindFadeFrameReads(byte[] img)
    {
        var list = new List<uint>();
        int end = TextEnd(img);
        foreach (string pattern in FadeFramePatterns)
        {
            int[] pat = pattern.Split(' ').Select(t => t == "??" ? -1 : Convert.ToInt32(t, 16)).ToArray();
            for (int i = 0x1000; i < end - pat.Length; i++)
            {
                int j = 0;
                while (j < pat.Length && (pat[j] < 0 || img[i + j] == pat[j])) j++;
                if (j == pat.Length) list.Add(ImageBase + (uint)i);
            }
        }
        // the stub reads the client from the first site, so all five have to use the same one
        if (list.Select(va => BitConverter.ToUInt32(img, (int)(va - ImageBase) + 2)).Distinct().Count() > 1) return new List<uint>();
        return list.Count == 5 ? list : new List<uint>();
    }

    // 13 bytes: mov ecx,[client] / mov r,[ecx] / mov r,[r+74h] / call r
    static void PatchFadeFrameReads(IntPtr proc, byte[] img, List<uint> sites, uint fpsVa, uint stubVa)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        uint client = BitConverter.ToUInt32(img, (int)(sites[0] + 2 - ImageBase));
        var s = new List<byte> { 0x8B, 0x0D }; s.AddRange(u(client));                 // mov ecx,[TheGameClient]
        s.AddRange(new byte[] { 0x8B, 0x01, 0xFF, 0x50, 0x74, 0x6B, 0xC0, 0x1E });   // call getFrame / imul eax,30
        s.AddRange(new byte[] { 0x8B, 0x15 }); s.AddRange(u(fpsVa));                  // mov edx,[fps]
        s.AddRange(new byte[] { 0x8D, 0x44, 0x10, 0xFF, 0x33, 0xD2, 0xF7, 0x35 }); s.AddRange(u(fpsVa));   // ceil(frame*30/fps)
        s.Add(0xC3);
        Write(proc, stubVa, s.ToArray());
        foreach (uint site in sites)
        {
            var p = new List<byte> { 0xE8 }; p.AddRange(u(stubVa - (site + 5))); p.AddRange(Enumerable.Repeat((byte)0x90, 8));
            uint old;
            if (!Protect(proc, (IntPtr)site, (UIntPtr)13, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, site, p.ToArray());
            Protect(proc, (IntPtr)site, (UIntPtr)13, old, out old);
        }
    }

    // scrolling moves a fixed step per frame. everything goes through scrollBy(Coord2D*) so
    // wrap it in the vtable and scale by 30/fps (same idea as CNCStuff/cnc3_fps_patch)
    const string ScrollByPattern = "A1 ?? ?? ?? ?? 83 EC 60 80 B8 BC 00 00 00 00 56 8B F1 74 06 80 7E 48 00 75 09 80 BE 35 27 00 00 00 74 09 33 C0 5E 83 C4 60 C2 04 00";

    const string ScrollByPatternCnc3 = "55 8B EC A1 ?? ?? ?? ?? 83 EC 64 80 B8 CC 00 00 00 00 53 8B D9 74 0D 80 7B 44 00 74 07 33 C0 E9";

    static uint FindScrollBySlot(byte[] img, out uint func)
    {
        func = FindUnique(img, ScrollByPattern);
        if (func == 0) func = FindUnique(img, ScrollByPatternCnc3);   // tw / kw
        if (func == 0) return 0;
        uint slot = 0;
        for (int o = TextEnd(img) & ~3; o + 4 <= img.Length; o += 4)
            if (BitConverter.ToUInt32(img, o) == func) { if (slot != 0) return 0; slot = ImageBase + (uint)o; }
        return slot;
    }

    static void PatchScrollBy(IntPtr proc, uint slot, uint func, int fps, uint scaleVa, uint tmpVa, uint stubVa)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        Write(proc, scaleVa, BitConverter.GetBytes(30f / fps));
        var s = new List<byte> { 0x8B, 0x44, 0x24, 0x04, 0xF3, 0x0F, 0x10, 0x00 };   // mov eax,[esp+4] / movss xmm0,[eax]
        s.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0x05 }); s.AddRange(u(scaleVa));    // mulss xmm0,[scale]
        s.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); s.AddRange(u(tmpVa));      // movss [tmp],xmm0
        s.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x40, 0x04 });                      // movss xmm0,[eax+4]
        s.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0x05 }); s.AddRange(u(scaleVa));    // mulss xmm0,[scale]
        s.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); s.AddRange(u(tmpVa + 4));  // movss [tmp+4],xmm0
        s.AddRange(new byte[] { 0xC7, 0x44, 0x24, 0x04 }); s.AddRange(u(tmpVa));      // mov [esp+4],tmp
        s.Add(0xE9); s.AddRange(u(func - (stubVa + (uint)s.Count + 4)));              // jmp scrollBy
        Write(proc, stubVa, s.ToArray());
        uint old;
        if (!Protect(proc, (IntPtr)slot, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("couldn't unprotect game memory");
        Write(proc, slot, u(stubVa));
        Protect(proc, (IntPtr)slot, (UIntPtr)4, old, out old);
    }

    // zoom extra: multiply the max zoom (550 default) by the ini value.
    // only when there's no network object so never online/lan
    const string ZoomMaxPattern = "8B 96 FC 26 00 00 8B 42 04 57 8D BE FC 26 00 00 8B CF FF D0 8B 17 8B 02 51 8B CF D9 1C 24 FF D0";
    const string NetObjectPattern = "8B 35 ?? ?? ?? ?? 3B F5 0F 84 ?? ?? ?? ?? 80 3D ?? ?? ?? ?? 00 0F 85 ?? ?? ?? ?? 80 3D ?? ?? ?? ?? 00 0F 85";

    static uint FindZoomSite(byte[] img, out uint netObject)
    {
        uint z = FindUnique(img, ZoomMaxPattern), n = FindUnique(img, NetObjectPattern);
        netObject = n != 0 ? BitConverter.ToUInt32(img, (int)(n + 2 - ImageBase)) : 0;
        return z != 0 && n != 0 ? z + 0xA : 0;
    }

    static void PatchZoom(IntPtr proc, uint site, uint netObject, float factor, uint factorVa, uint stubVa)
    {
        Write(proc, factorVa, BitConverter.GetBytes(factor));
        var s = new Asm(stubVa);
        s.E(0x8D, 0xBE, 0xFC, 0x26, 0, 0, 0x8B, 0xCF, 0xFF, 0xD0);   // original code, st0 = max zoom
        s.E(0x83, 0x3D); s.D(netObject); s.E(0x00); s.J(0x75, "online");
        s.E(0xD8, 0x0D); s.D(factorVa);                              // fmul [factor]
        s.L("online");
        s.E(0xC3);
        Write(proc, stubVa, s.Done(0x40));
        var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stubVa - (site + 5))); p.AddRange(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        uint old;
        if (!Protect(proc, (IntPtr)site, (UIntPtr)10, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("couldn't unprotect game memory");
        Write(proc, site, p.ToArray());
        Protect(proc, (IntPtr)site, (UIntPtr)10, old, out old);
    }

    // held camera keys (numpad zoom / rotate) do one step per drawn frame, so at 120 fps they went 4x too fast (#15).
    // zoom: the held-key behaviors call the view's zoomIn/zoomOut (z*0.96 - 1, z*1.05 + 1). they get their own copy
    // of those two with the step for this fps, the mouse wheel still calls the originals (one full step per notch).
    // rotate: rate * constant each frame, constant gets * 30/fps. ra3 only for now
    const string HeldZoomPattern = "80 7C 24 08 00 74 10 8B 0D ?? ?? ?? ?? 8B 01 8B 90 ?? ?? 00 00 FF D2 C2 10 00";
    const string ViewZoomInPattern = "56 8B F1 57 8B 3E 8B 87 ?? ?? 00 00 FF D0 D8 0D ?? ?? ?? ?? 8B 97 ?? ?? 00 00 51 D8 25 ?? ?? ?? ?? 8B CE D9 1C 24 FF D2 5F 5E C3";
    const string ViewZoomOutPattern = "56 8B F1 57 8B 3E 8B 87 ?? ?? 00 00 FF D0 D8 0D ?? ?? ?? ?? 8B 97 ?? ?? 00 00 51 D8 05 ?? ?? ?? ?? 8B CE D9 1C 24 FF D2 5F 5E C3";
    const string HeldRotatePattern = "80 7C 24 08 00 74 ?? F3 0F 10 41 04 8B 44 24 10 F3 0F 59 05 ?? ?? ?? ?? F3 0F 58 00 F3 0F 11 00 C2 10 00";

    // tw / kw: same idea, other registers. rotate speed comes from GlobalData instead of a constant
    const string HeldZoomPatternCnc3 = "80 7C 24 08 00 74 0E 8B 0D ?? ?? ?? ?? 8B 01 FF 90 ?? 01 00 00 C2 10 00";
    const string ViewZoomInPatternCnc3 = "56 57 8B F9 8B 37 FF 96 ?? ?? 00 00 D8 0D ?? ?? ?? ?? 51 8B CF D8 25 ?? ?? ?? ?? D9 1C 24 FF 96 ?? ?? 00 00 5F 5E C3";
    const string ViewZoomOutPatternCnc3 = "56 57 8B F9 8B 37 FF 96 ?? ?? 00 00 D8 0D ?? ?? ?? ?? 51 8B CF D8 05 ?? ?? ?? ?? D9 1C 24 FF 96 ?? ?? 00 00 5F 5E C3";
    const string HeldRotatePatternCnc3 = "80 7C 24 08 00 74 1E A1 ?? ?? ?? ?? F3 0F 10 80 ?? ?? 00 00 8B 44 24 10 F3 0F 59 41 04 F3 0F 58 00 F3 0F 11 00 C2 10 00";

    // Cnc3: tw/kw layout. MOff/COff = where the zoom step's two float operands sit in the view function
    class HeldCamera { public uint ZoomIn, ZoomOut, ViewIn, ViewOut, Rotate; public bool Cnc3; public int Len = 43, MOff = 16, COff = 29; }

    static List<uint> FindAll(byte[] img, string pattern)
    {
        int[] pat = pattern.Split(' ').Select(t => t == "??" ? -1 : Convert.ToInt32(t, 16)).ToArray();
        int end = TextEnd(img);
        var list = new List<uint>();
        for (int i = 0x1000; i < end - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && (pat[j] < 0 || img[i + j] == pat[j])) j++;
            if (j == pat.Length) list.Add(ImageBase + (uint)i);
        }
        return list;
    }

    static float FloatAt(byte[] img, uint va)
    {
        int o = (int)(va - ImageBase);
        return o > 0 && o + 4 <= img.Length ? BitConverter.ToSingle(img, o) : float.NaN;
    }

    static HeldCamera FindHeldCamera(byte[] img)
    {
        Func<uint, int, uint> dw = (va, off) => BitConverter.ToUInt32(img, (int)(va - ImageBase) + off);
        bool cnc3 = false;
        var pair = FindAll(img, HeldZoomPattern);
        if (pair.Count == 0) { pair = FindAll(img, HeldZoomPatternCnc3); cnc3 = true; }
        int gap = cnc3 ? 0x18 : 0x20;
        if (pair.Count != 2 || pair[1] != pair[0] + gap || dw(pair[0], 9) != dw(pair[1], 9) || dw(pair[1], 17) != dw(pair[0], 17) + 4)
            return null;   // zoom in, then zoom out right after: same view, next slot
        var h = cnc3
            ? new HeldCamera { ZoomIn = pair[0], ZoomOut = pair[1], ViewIn = FindUnique(img, ViewZoomInPatternCnc3), ViewOut = FindUnique(img, ViewZoomOutPatternCnc3),
                               Rotate = FindUnique(img, HeldRotatePatternCnc3), Cnc3 = true, Len = 39, MOff = 14, COff = 23 }
            : new HeldCamera { ZoomIn = pair[0], ZoomOut = pair[1], ViewIn = FindUnique(img, ViewZoomInPattern), ViewOut = FindUnique(img, ViewZoomOutPattern), Rotate = FindUnique(img, HeldRotatePattern) };
        if (h.ViewIn == 0 || h.ViewOut == 0 || h.Rotate == 0) return null;
        // the stock steps, so we know we have the right functions
        if (Math.Abs(FloatAt(img, dw(h.ViewIn, h.MOff)) - 0.96f) > 1e-4 || Math.Abs(FloatAt(img, dw(h.ViewOut, h.MOff)) - 1.05f) > 1e-4) return null;
        if (FloatAt(img, dw(h.ViewIn, h.COff)) != 1f || FloatAt(img, dw(h.ViewOut, h.COff)) != 1f) return null;
        // the view's function table has zoomIn / zoomOut next to each other
        bool table = false;
        for (int o = TextEnd(img); o + 8 <= img.Length && !table; o += 4)
            table = BitConverter.ToUInt32(img, o) == h.ViewIn && BitConverter.ToUInt32(img, o + 4) == h.ViewOut;
        return table ? h : null;
    }

    // tw / kw effects that count drawn frames (all found by CNCStuff/cnc3_fps_patch, same signatures).
    // 1/30 per frame steps: camera moves and lasers (same constant as the model step)
    const string CameraStepPattern = "80 BB C8 00 00 00 00 75 6F D9 05 ?? ?? ?? ?? 51";
    const string LaserStepPattern = "0F 2F F1 F3 0F 10 1D ?? ?? ?? ?? F3 0F 2A E0 0F 28 EC F3 0F 59 EB";
    // absolute frame reads (getFrame = slot 78h here) where the art is made in 30fps frames
    const string TracerResetPattern = "8B 01 FF 50 78 89 86 90 00 00 00 5E C3";
    const string TracerUpdatePattern = "8B 01 57 FF 50 78 3B 86 90 00 00 00";
    const string CloudPattern = "8B 01 FF 50 78 33 DB 32 C9 39 46 70";
    const string Anim2DSetPattern = "66 89 46 04 8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 89 46 08";
    const string Anim2DUpdatePattern = "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 2B 46 08 3B 46 18";
    // cpu particles step once per call
    const string ParticlePatternCnc3 = "8B F1 E8 ?? ?? ?? ?? 83 66 40 00 6A 02 8D BE 8C 00 00 00 5B FF 77 04 8B CF";
    // ability placement circles: throb period in ms * 0.03 frames/ms, compared with the drawn frame
    const string ThrobPattern = "8B 0D ?? ?? ?? ?? 8B 01 57 FF 50 78 8B F8 8B 06 D9 40 14 51 D8 0D ?? ?? ?? ?? 51 DD 1C 24";
    // camera shake: amplitude *= 0.75 every frame
    const string ShakePattern = "A1 ?? ?? ?? ?? 80 B8 44 0F 00 00 00 74 09 80 B8 45 0F 00 00 00 74 22 F3 0F 59 05 ?? ?? ?? ?? F3 0F 11 43 78";

    // drawable fades (ion cannon ripple, dying units etc): start stamped with getFrame, the update adds getFrame - start
    // to the progress, length in 30fps frames. same as the ra3 fades fix
    const string Cnc3FadeSetPattern = "89 86 20 02 00 00 8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 89 86 54 04 00 00";
    const string Cnc3FadeSet2Pattern = "89 86 24 02 00 00 8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 89 86 54 04 00 00";
    const string Cnc3FadeUpdatePattern = "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 8B D0 8D 8E 54 04 00 00 2B 11 89 01";
    // a second drawable timer: value += max(1, frames since last) * rate. the max(1) would still step every drawn
    // frame, so it becomes max(0, ...) with the 30hz frame (= stock at 30)
    const string Cnc3PulseSetPattern = "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 89 86 58 04 00 00 5E C2 08 00";
    const string Cnc3PulseUpdatePattern = "8B 0D ?? ?? ?? ?? 8B 01 53 FF 50 78 8B C8 2B 8E 58 04 00 00 33 D2 42 3B CA";

    class Cnc3Fx { public uint CameraStep, LaserStep, Throb, Shake, PfxSite, PfxSim; public List<uint> Frame5 = new List<uint>(); public uint TracerUpdate;
                   public List<uint> Fades = new List<uint>(); public uint PulseSet, PulseUpdate; }

    static Cnc3Fx FindCnc3Fx(byte[] img, uint modelStep)
    {
        Func<uint, int, uint> dw = (va, off) => BitConverter.ToUInt32(img, (int)(va - ImageBase) + off);
        var fx = new Cnc3Fx();
        uint cam = FindUnique(img, CameraStepPattern), laser = FindUnique(img, LaserStepPattern);
        // only if they read the same 1/30 the model step reads
        if (cam != 0 && laser != 0 && modelStep != 0 && dw(cam, 11) == dw(modelStep, 0) && dw(laser, 7) == dw(modelStep, 0))
        { fx.CameraStep = cam + 11; fx.LaserStep = laser + 7; }
        uint tr = FindUnique(img, TracerResetPattern), tu = FindUnique(img, TracerUpdatePattern), cl = FindUnique(img, CloudPattern);
        uint aset = FindUnique(img, Anim2DSetPattern), aupd = FindUnique(img, Anim2DUpdatePattern);
        if (tr != 0 && tu != 0) { fx.Frame5.Add(tr); fx.TracerUpdate = tu; }
        if (cl != 0) fx.Frame5.Add(cl);
        if (aset != 0 && aupd != 0) { fx.Frame5.Add(aset + 10); fx.Frame5.Add(aupd + 6); }   // both or neither (they get subtracted)
        uint p = FindUnique(img, ParticlePatternCnc3);
        if (p != 0) { fx.PfxSite = p + 2; fx.PfxSim = p + 7 + dw(p, 3); }
        uint th = FindUnique(img, ThrobPattern);
        // framesPerMs: a global set to 0.03 at startup, nothing to check in the file. just make sure it isn't code
        if (th != 0 && dw(th, 22) >= ImageBase + (uint)TextEnd(img)) fx.Throb = th + 22;
        // fades: all four or none (mixing clocks breaks the subtraction)
        var fs = FindAll(img, Cnc3FadeSetPattern);
        uint fs2 = FindUnique(img, Cnc3FadeSet2Pattern), fu = FindUnique(img, Cnc3FadeUpdatePattern);
        if (fs.Count == 2 && fs2 != 0 && fu != 0) fx.Fades.AddRange(new[] { fs[0] + 12, fs[1] + 12, fs2 + 12, fu + 6 });
        uint ps = FindUnique(img, Cnc3PulseSetPattern), pu = FindUnique(img, Cnc3PulseUpdatePattern);
        if (ps != 0 && pu != 0) { fx.PulseSet = ps + 6; fx.PulseUpdate = pu + 6; }
        uint sh = FindUnique(img, ShakePattern);
        if (sh != 0 && FloatAt(img, dw(sh, 27)) == 0.75f) fx.Shake = sh + 27;
        return fx;
    }

    // stepVa holds 1/fps already. stubVa: 30hz frame stub (slot 78h), dataVa: 2 floats
    static void PatchCnc3Fx(IntPtr proc, Cnc3Fx fx, int fps, uint fpsVa, uint stepVa, uint stubVa, uint dataVa)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        if (fx.CameraStep != 0 && On("camsteps")) Redirect(proc, new List<uint> { fx.CameraStep, fx.LaserStep }, stepVa);
        bool fades = fx.Fades.Count > 0 && On("fades"), pulse = fx.PulseSet != 0 && On("fades");
        if ((fx.Frame5.Count > 0 || fx.TracerUpdate != 0) && On("fxframes") || fades || pulse)
        {
            var s = new List<byte> { 0x8B, 0x01, 0xFF, 0x50, 0x78, 0x6B, 0xC0, 0x1E };   // mov eax,[ecx] / call [eax+78h] / imul eax,30
            s.AddRange(new byte[] { 0x8B, 0x15 }); s.AddRange(u(fpsVa));                // mov edx,[fps]
            s.AddRange(new byte[] { 0x8D, 0x44, 0x10, 0xFF, 0x33, 0xD2, 0xF7, 0x35 }); s.AddRange(u(fpsVa));   // ceil(frame*30/fps)
            s.Add(0xC3);
            Write(proc, stubVa, s.ToArray());
            // mov eax,[ecx] / call [eax+78h] (5 bytes) -> call stub
            var five = new List<uint>();
            if (On("fxframes")) five.AddRange(fx.Frame5);
            if (fades) five.AddRange(fx.Fades);
            if (pulse) five.Add(fx.PulseSet);
            foreach (uint site in five)
            {
                var p = new List<byte> { 0xE8 }; p.AddRange(u(stubVa - (site + 5)));
                Write(proc, site, p.ToArray());
            }
            // mov eax,[ecx] / push edi / call [eax+78h] (6 bytes) -> push edi / call stub
            if (fx.TracerUpdate != 0 && On("fxframes"))
            {
                var p = new List<byte> { 0x57, 0xE8 }; p.AddRange(u(stubVa - (fx.TracerUpdate + 6)));
                Write(proc, fx.TracerUpdate, p.ToArray());
            }
            // mov eax,[ecx] / push ebx / call [eax+78h] -> push ebx / call stub, and inc edx (the max 1) -> nop
            if (pulse)
            {
                var p = new List<byte> { 0x53, 0xE8 }; p.AddRange(u(stubVa - (fx.PulseUpdate + 6)));
                Write(proc, fx.PulseUpdate, p.ToArray());
                Write(proc, fx.PulseUpdate + 16, new byte[] { 0x90 });
            }
        }
        if (fx.Throb != 0 && On("throb"))
        {
            Write(proc, dataVa, BitConverter.GetBytes(fps / 1000f));   // frames per ms at this fps
            Redirect(proc, new List<uint> { fx.Throb }, dataVa);
        }
        if (fx.Shake != 0 && On("shake"))
        {
            Write(proc, dataVa + 4, BitConverter.GetBytes((float)Math.Pow(0.75, 30.0 / fps)));   // same decay per second
            Redirect(proc, new List<uint> { fx.Shake }, dataVa + 4);
        }
    }

    static void PatchHeldCamera(IntPtr proc, byte[] img, HeldCamera h, int fps, uint dataVa, uint stubVa)
    {
        double k = 30.0 / fps;
        var zooms = new[] { new { Beh = h.ZoomIn, Fn = h.ViewIn }, new { Beh = h.ZoomOut, Fn = h.ViewOut } };
        for (int i = 0; i < 2; i++)
        {
            // z = m*z +/- c per frame. same result per second at fps frames: m' = m^(30/fps), c' = c*(1-m')/(1-m)
            byte[] code = img.Skip((int)(zooms[i].Fn - ImageBase)).Take(h.Len).ToArray();
            double m = FloatAt(img, BitConverter.ToUInt32(code, h.MOff)), c = FloatAt(img, BitConverter.ToUInt32(code, h.COff));
            double m2 = Math.Pow(m, k), c2 = c * (1 - m2) / (1 - m);
            uint mVa = dataVa + (uint)(i * 8), cVa = mVa + 4, stub = stubVa + (uint)(i * 0x40);
            Write(proc, mVa, BitConverter.GetBytes((float)m2));
            Write(proc, cVa, BitConverter.GetBytes((float)c2));
            BitConverter.GetBytes(mVa).CopyTo(code, h.MOff);   // fmul [m']
            BitConverter.GetBytes(cVa).CopyTo(code, h.COff);   // fsub/fadd [c']
            Write(proc, stub, code);
            // behavior: mov ecx,[view] / (mov eax,[ecx] / mov edx,[eax+slot] / call edx) -> call stub
            // tw/kw: (mov eax,[ecx] / call [eax+slot]), 8 bytes
            uint site = zooms[i].Beh + 13;
            var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stub - (site + 5))); p.AddRange(Enumerable.Repeat((byte)0x90, h.Cnc3 ? 3 : 5));
            Write(proc, site, p.ToArray());
        }
        if (h.Cnc3)
        {
            // movss xmm0,[gd+off] / mov eax,[esp+10h] / mulss xmm0,[ecx+4] (17 bytes) -> call stub that also scales by 30/fps
            uint site = h.Rotate + 12, rstub = stubVa + 0x80;
            Write(proc, dataVa + 0x10, BitConverter.GetBytes((float)k));
            var s = new List<byte>(img.Skip((int)(site - ImageBase)).Take(8));                     // movss xmm0,[eax+off]
            s.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0x41, 0x04, 0xF3, 0x0F, 0x59, 0x05 });         // mulss xmm0,[ecx+4] / mulss xmm0,[k]
            s.AddRange(BitConverter.GetBytes(dataVa + 0x10));
            s.AddRange(new byte[] { 0x8B, 0x44, 0x24, 0x14, 0xC3 });                                // mov eax,[esp+14h] (the call pushed 4) / ret
            Write(proc, rstub, s.ToArray());
            var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(rstub - (site + 5))); p.AddRange(Enumerable.Repeat((byte)0x90, 12));
            Write(proc, site, p.ToArray());
            return;
        }
        uint rotOperand = h.Rotate + 0x14;
        float rot = FloatAt(img, BitConverter.ToUInt32(img, (int)(rotOperand - ImageBase)));
        Write(proc, dataVa + 0x10, BitConverter.GetBytes((float)(rot * k)));
        Redirect(proc, new List<uint> { rotOperand }, dataVa + 0x10);
    }

    // units only interpolate for 6 frames after a logic update (enough at 90fps). above that
    // they froze then jumped = the hitching. make it fps/15 + 2
    static readonly string[] InterpWindowPatterns = {
        "2B 86 30 01 00 00 83 F8 06",                                            // ra3
        "FF 50 78 2B 83 38 01 00 00 83 F8 06", "FF 50 78 2B 86 38 01 00 00 83 F8 06",   // tw
        "FF 50 78 2B 07 83 F8 06", "FF 50 78 2B 06 83 F8 06" };                   // kw

    static List<uint> FindInterpWindow(byte[] img)
    {
        var list = new List<uint>();
        int end = TextEnd(img);
        foreach (string pattern in InterpWindowPatterns)
        {
            int[] pat = pattern.Split(' ').Select(x => Convert.ToInt32(x, 16)).ToArray();
            for (int i = 0x1000; i < end - pat.Length; i++)
            {
                int j = 0;
                while (j < pat.Length && img[i + j] == pat[j]) j++;
                if (j == pat.Length) list.Add(ImageBase + (uint)(i + pat.Length - 1));
            }
        }
        // ra3 has 2, uprising 3 (an extra copy with a "force" argument), all the same check on the same field
        return list.Count == 2 || list.Count == 3 ? list : new List<uint>();
    }

    static void PatchInterpWindow(IntPtr proc, List<uint> sites, int fps)
    {
        byte frames = (byte)Math.Min(127, Math.Max(6, fps / 15 + 2));
        foreach (uint s in sites)
        {
            uint old;
            if (!Protect(proc, (IntPtr)s, (UIntPtr)1, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, s, new[] { frames });
            Protect(proc, (IntPtr)s, (UIntPtr)1, old, out old);
        }
    }

    // mini assembler for stubs
    class Asm
    {
        public readonly List<byte> B = new List<byte>();
        readonly uint org;
        readonly Dictionary<string, int> labels = new Dictionary<string, int>();
        readonly List<Tuple<int, string>> fixes = new List<Tuple<int, string>>();
        public Asm(uint org) { this.org = org; }
        public void E(params byte[] b) { B.AddRange(b); }
        public void D(uint v) { B.AddRange(BitConverter.GetBytes(v)); }
        public void L(string name) { labels[name] = B.Count; }
        // pass the short opcode, always emits rel32
        public void J(byte op, string name)
        {
            if (op == 0xEB) B.Add(0xE9); else { B.Add(0x0F); B.Add((byte)(0x80 + (op - 0x70))); }
            fixes.Add(Tuple.Create(B.Count, name)); D(0);
        }
        public void Rel(byte op, uint target) { B.Add(op); D(target - (org + (uint)B.Count + 4)); }
        public byte[] Done(int max)
        {
            foreach (var f in fixes)
            {
                int rel = labels[f.Item2] - (f.Item1 + 4);
                byte[] r = BitConverter.GetBytes(rel);
                for (int i = 0; i < 4; i++) B[f.Item1 + i] = r[i];
            }
            if (B.Count > max) throw new Exception("stub too big");
            return B.ToArray();
        }
    }

    // each 15hz tick is 6 phases. at 90+ fps the engine does one phase per frame so a tick = 6 frames,
    // meaning 120fps ran 20 ticks/s and slow pcs went slow-mo. so: schedule ticks off the clock instead
    // (every 66.67ms, in 1/3ms units so it's exact), run whatever phases are due each frame, and set
    // interp from the time so movement stays smooth
    // PreGlobal/PreOff/PreFn: bfme2 calls PreFn([[PreGlobal]+PreOff] * 10 + phase - 1) before each dispatch
    class SchedSite { public uint Advance, Exit, TimeFn; public byte Phase, Interp; public uint Dispatch; public uint PreGlobal, PreFn; public byte PreOff; public int Tick = 200; }   // Tick: logic tick in 1/3 ms (200 = 15 hz, 600 = 5 hz)

    const string SchedPatternA = "8B 0D ?? ?? ?? ?? BB 01 00 00 00 88 99 C4 00 00 00 8B 4E 58 83 F9 06 75 1A 80 7E 64 00 74 14 A1 ?? ?? ?? ?? 33 D2 F7 35";
    const string SchedPatternB = "8B 16 50 8B 82 90 00 00 00 8B CE FF D0 8B 0D ?? ?? ?? ?? 8B 11 8B 82 A0 00 00 00 5F 5E 5B 83 C4 04";
    const string SchedPatternCnc3A = "33 DB 43 88 98 D4 00 00 00 8B 4E 40 83 F9 06 75 1A 80 7E 4C 00 74 14 A1";
    const string SchedPatternCnc3B = "8B 16 50 FF 92 94 00 00 00 8B 0D ?? ?? ?? ?? 8B 01 FF 90";

    static uint CallTarget(byte[] img, uint va)
    {
        if (va == 0 || img[va - ImageBase] != 0xE8) return 0;
        return va + 5 + BitConverter.ToUInt32(img, (int)(va + 1 - ImageBase));
    }

    // fps/logic then cmp 6 (phase dispatcher + tick boundary)
    static bool IsPhaseRatioSite(byte[] img, uint site)
    {
        int o = (int)(site - ImageBase) + 4;
        if (o + 20 > img.Length || img[o] != 0x33 || img[o + 1] != 0xD2 || img[o + 2] != 0xF7 || img[o + 3] != 0x35) return false;
        for (int i = o + 8; i < o + 16; i++)
            if ((img[i] == 0x83 && (img[i + 1] & 0xF8) == 0xF8 && img[i + 2] == 0x06) || (img[i] == 0x6A && img[i + 1] == 0x06)) return true;
        return false;
    }

    static SchedSite FindScheduler(byte[] img)
    {
        uint a = FindUnique(img, SchedPatternA), b = FindUnique(img, SchedPatternB), lim = FindUnique(img, LimiterPatternRA3);
        if (a != 0 && b != 0 && b - a == 0x124 && lim != 0)   // ra3
        {
            var s = new SchedSite { Advance = a + 0x11, Exit = b + 0xD, Phase = 0x58, Interp = 0x60, Dispatch = 0x90, TimeFn = CallTarget(img, lim - 0x27) };
            return s.TimeFn != 0 ? s : null;
        }
        a = FindUnique(img, SchedPatternCnc3A); b = FindUnique(img, SchedPatternCnc3B); lim = FindUnique(img, LimiterPatternCnc3);
        if (a != 0 && b != 0 && (b + 9) - (a + 9) == 0xCF && lim != 0)   // tw / kw
        {
            var s = new SchedSite { Advance = a + 9, Exit = b + 9, Phase = 0x40, Interp = 0x48, Dispatch = 0x94, TimeFn = CallTarget(img, lim + 20) };
            return s.TimeFn != 0 ? s : null;
        }
        return null;
    }

    static void PatchScheduler(IntPtr proc, byte[] img, SchedSite site, uint fpsVa, uint mem)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        uint eng = mem + 0x3C, r = mem + 0x38, now3 = mem + 0x80, t0 = mem + 0x84, pend = mem + 0x88, k200 = mem + 0x8C;
        uint stubA = mem + 0x800, stubB = mem + 0xA00, table = mem + 0xB0, tickFrame = mem + 0xC8, prevNow = mem + 0xCC, prevA = mem + 0xD0;
        // phase k is due once (time into the tick + this frame) reaches k/6, the moment stock has interp = k/6.
        // interp then rises smoothly from k/6 toward (k+1)/6 until the next phase. at 90 that's stock exactly.
        // (it used to run phase k at (k-1)/6, so interp sat ~1/6 behind the phase that just ran: turrets wobbled, #11)
        int T = site.Tick;   // 1/3 ms per logic tick
        for (int k = 1; k <= 6; k++) Write(proc, table + (uint)((k - 1) * 4), BitConverter.GetBytes(k * T / 6 - 3));   // 30 63 97 130 163 197 at 15 hz
        uint exitGlobal = BitConverter.ToUInt32(img, (int)(site.Exit + 2 - ImageBase));
        byte ph = site.Phase;
        Write(proc, k200, BitConverter.GetBytes((float)T));

        // stub A: replaces mov ecx,[esi+phase] / cmp ecx,6
        var a = new Asm(stubA);
        a.E(0x89, 0x35); a.D(eng);                                   // mov [engine],esi
        a.E(0x50, 0x52, 0x51);                                       // push eax / push edx / push ecx
        a.E(0xA1); a.D(fpsVa); a.E(0x33, 0xD2, 0xB9, 0x0F, 0, 0, 0, 0xF7, 0xF1); a.E(0xA3); a.D(r);   // r = fps / 15
        a.Rel(0xE8, site.TimeFn);                                   // eax = ms (timeGetTime)
        a.E(0x8D, 0x04, 0x40); a.E(0xA3); a.D(now3);                 // now3 = ms * 3
        a.E(0x83, 0x3D); a.D(t0); a.E(0x00); a.J(0x75, "have");     // first run
        a.E(0xA3); a.D(t0);
        a.L("have");
        a.E(0x8B, 0x4E, ph);                                         // ecx = phase
        a.E(0x83, 0x3D); a.D(pend); a.E(0x00); a.J(0x74, "nopend");  // started a tick last frame?
        a.E(0xC7, 0x05); a.D(pend); a.D(0);
        a.E(0x83, 0xF9, 0x06); a.J(0x72, "nopend");                 // wrapped = it started
        a.E(0x81, 0x2D); a.D(t0); a.D((uint)T);                      // network held it back, undo
        a.L("nopend");
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // edx = t = now3 - tickStart
        a.E(0x50, 0x2B, 0x05); a.D(prevA);                           // push eax / eax = frame length
        a.E(0xFF, 0x35); a.D(now3); a.E(0x8F, 0x05); a.D(prevA);    // prevA = now3
        a.E(0x85, 0xC0); a.J(0x7D, "lpos"); a.E(0x33, 0xC0);
        a.L("lpos");
        a.E(0x83, 0xF8, 0x64); a.J(0x7E, "lok"); a.E(0xB8); a.D(100);   // clamp 0..100
        a.L("lok");
        a.E(0x03, 0xD0, 0x58);                                       // edx = t + frame length / pop eax
        a.E(0x83, 0xF9, 0x06); a.J(0x72, "mid");
        // phase 6 done. phase 1 of the next tick is due at 200 + 33 (thresholds are 1ms early, now3 rounds down)
        a.E(0x81, 0xFA); a.D((uint)(T + T / 6 - 3)); a.J(0x7C, "idle");
        a.E(0x81, 0x05); a.D(t0); a.D((uint)T);                      // tickStart += one tick
        a.E(0xA3); a.D(tickFrame);
        a.E(0xC7, 0x05); a.D(pend); a.D(1);
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // >2 ticks behind, resync
        a.E(0x81, 0xFA); a.D((uint)(2 * T)); a.J(0x7E, "pass");
        a.E(0xA3); a.D(t0); a.J(0xEB, "pass");
        a.L("mid");
        // count timetable entries <= t. phases 3-6 are the object buckets so spread them out
        a.E(0x33, 0xC0);
        a.L("scan");
        a.E(0x3B, 0x14, 0x85); a.D(table); a.J(0x7C, "counted");      // cmp edx,[T + eax*4] / jl
        a.E(0x40, 0x83, 0xF8, 0x06); a.J(0x72, "scan");
        a.L("counted");
        a.E(0x3B, 0xC1); a.J(0x76, "idle");                          // nothing due
        a.L("loop");                                                 // all but the last one
        a.E(0x8D, 0x51, 0x01, 0x3B, 0xD0); a.J(0x73, "pass");
        a.E(0x89, 0x56, ph, 0x50);                                   // [phase]=n / push eax
        if (site.PreFn != 0)
        {
            a.E(0x8B, 0x0D); a.D(site.PreGlobal); a.E(0x8B, 0x49, site.PreOff, 0x6B, 0xC9, 0x0A, 0x8D, 0x4C, 0x11, 0xFF);   // ecx = [[g]+off]*10 + n - 1
            a.E(0x52, 0x51); a.Rel(0xE8, site.PreFn); a.E(0x83, 0xC4, 0x04, 0x5A);   // push n / push ecx / call / add esp,4 / pop n
        }
        a.E(0x52, 0x8B, 0xCE, 0x8B, 0x16, 0xFF, 0x92); a.D(site.Dispatch);   // dispatch(n)
        a.E(0x58, 0x8B, 0x4E, ph); a.J(0xEB, "loop");
        a.L("idle");
        a.E(0x59, 0x5A, 0x58, 0x83, 0xC4, 0x04, 0x57); a.Rel(0xE9, site.Exit);
        a.L("pass");                                                 // back to original
        a.E(0x59, 0x5A, 0x58, 0x8B, 0x4E, ph, 0x83, 0xF9, 0x06, 0xC3);
        Write(proc, stubA, a.Done(0x200));

        // stub B: replaces mov ecx,[global] at the exit.
        // interp = (now - tickStart + frame length) / 200, max 1
        var b = new Asm(stubB);
        b.E(0x50, 0x52, 0x51);
        b.E(0xA1); b.D(now3); b.E(0x8B, 0xD0, 0x2B, 0x15); b.D(prevNow);   // edx = frame length
        b.E(0xA3); b.D(prevNow);
        b.E(0x85, 0xD2); b.J(0x7D, "dpos"); b.E(0x33, 0xD2);
        b.L("dpos");
        b.E(0x83, 0xFA, 0x64); b.J(0x7E, "dok"); b.E(0xBA); b.D(100);         // clamp 0..100
        b.L("dok");
        b.E(0x2B, 0x05); b.D(t0); b.E(0x03, 0xC2);                           // eax = now - tickStart + frame length
        b.E(0x85, 0xC0); b.J(0x7D, "b1"); b.E(0x33, 0xC0);
        b.L("b1");
        b.E(0x3D); b.D((uint)T); b.J(0x7E, "b2"); b.E(0xB8); b.D((uint)T);
        b.L("b2");
        // keep it between phase/6 and (phase+1)/6, it never runs ahead of what logic did
        b.E(0x8B, 0x56, ph, 0x8D, 0x4A, 0xFF, 0x83, 0xF9, 0x05); b.J(0x77, "b4");     // edx = phase / ecx = phase-1 / ja (not 1..6)
        b.E(0x3B, 0x04, 0x8D); b.D(table); b.J(0x7D, "b3"); b.E(0x8B, 0x04, 0x8D); b.D(table);   // eax = max(eax, T[phase-1])
        b.L("b3");
        b.E(0x83, 0xFA, 0x06); b.J(0x7D, "b4");                                    // phase 6: max stays 200
        b.E(0x3B, 0x04, 0x95); b.D(table); b.J(0x7E, "b4"); b.E(0x8B, 0x04, 0x95); b.D(table);   // eax = min(eax, T[phase])
        b.L("b4");
        b.E(0x50, 0xDB, 0x04, 0x24, 0xD8, 0x35); b.D(k200); b.E(0xD9, 0x5E, site.Interp, 0x58);   // [interp] = eax / 200
        b.E(0x59, 0x5A, 0x58, 0x8B, 0x0D); b.D(exitGlobal); b.E(0xC3);        Write(proc, stubB, b.Done(0x100));

        // skip=schedinterp: keep the game's own interp (phase / 6). for tracking down turret wobble (#11)
        var hooks = new List<Tuple<uint, uint>> { Tuple.Create(site.Advance, stubA) };
        if (On("schedinterp")) hooks.Add(Tuple.Create(site.Exit, stubB));
        foreach (var s in hooks)
        {
            var p = new List<byte> { 0xE8 }; p.AddRange(u(s.Item2 - (s.Item1 + 5))); p.Add(0x90);
            uint old;
            if (!Protect(proc, (IntPtr)s.Item1, (UIntPtr)6, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, s.Item1, p.ToArray());
            Protect(proc, (IntPtr)s.Item1, (UIntPtr)6, old, out old);
        }
    }

    // soviet/empire construction (StructureUnpackUpdate). the module's field offsets differ in uprising (wildcards).
    // start + duration are in 30fps frames but
    // "now" is getFrame() = drawn frames, so at 120 it's 4x ahead and builds pop in instantly.
    // use the logic tick through the same conversion instead
    const string UnpackPattern =
        "8B 35 ?? ?? ?? ?? 8B 57 ?? D9 86 BC 01 00 00 55 D9 5C 24 14 52 8B CE E8 ?? ?? ?? ?? D8 0D ?? ?? ?? ?? " +
        "D9 7C 24 12 8B CE 0F B7 44 24 12 D8 0D ?? ?? ?? ?? 0D 00 0C 00 00 89 44 24 18 8B 47 ?? D8 4C 24 14 50 " +
        "D9 6C 24 1C DF 7C 24 1C 8B 6C 24 1C D9 6C 24 16 E8 ?? ?? ?? ?? D8 0D ?? ?? ?? ?? 8B 4F ?? D9 7C 24 12 " +
        "0F B7 44 24 12 D8 0D ?? ?? ?? ?? 0D 00 0C 00 00 89 44 24 18 51 D8 4C 24 18 8B CE D9 6C 24 1C DF 7C 24 1C " +
        "8B 5C 24 1C D9 6C 24 16 E8 ?? ?? ?? ?? D8 0D ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? D9 7C 24 12 0F B7 44 24 12 " +
        "D8 0D ?? ?? ?? ?? 0D 00 0C 00 00 89 44 24 18 8B 01 D8 4C 24 14 D9 6C 24 18 DF 7C 24 18 8B 54 24 18 " +
        "89 54 24 14 8B 50 74 D9 6C 24 12 FF D2 8B F0 2B F5 5D 80 7F 45 00 74 22 83 7C 24 24 01 75 0A " +
        "A1 ?? ?? ?? ?? 8B 40 50 EB 0D 8B 0D ?? ?? ?? ?? 8B 11 8B 42 74 FF D0 2B D8";

    static uint FindUnpackProgress(byte[] img)
    {
        int[] pat = UnpackPattern.Split(' ').Select(t => t == "??" ? -1 : Convert.ToInt32(t, 16)).ToArray();
        int end = TextEnd(img), found = -1;
        for (int i = 0x1000; i < end - pat.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && (pat[j] < 0 || img[i + j] == pat[j])) j++;
            if (j < pat.Length) continue;
            if (found >= 0) return 0;
            found = i;
        }
        return found < 0 ? 0 : ImageBase + (uint)found;
    }

    // fine mode (engineVar set by the scheduler): 1/240s units + tick fraction so it's smooth
    static void PatchUnpack(IntPtr proc, byte[] img, uint block, uint stubVa, uint engineVar, uint fpmFineVa)
    {
        Func<uint, uint> rd = va => BitConverter.ToUInt32(img, (int)(va - ImageBase));
        Func<uint, byte[]> u = BitConverter.GetBytes;
        uint logic = rd(block + 2), conv = block + 0x17 + 5 + rd(block + 0x18), k1000 = rd(block + 0x1E), fpm = rd(block + 0x2F);
        bool fine = engineVar != 0 && img[conv + 0x16 - ImageBase] == 0xD8 && img[conv + 0x17 - ImageBase] == 0x0D
                    && rd(block + 0x6D) == fpm && rd(block + 0xAD) == fpm;
        if (fine)
        {
            uint spf = rd(conv + 0x18);   // seconds per logic frame
            Write(proc, fpmFineVa, BitConverter.GetBytes(0.24f));   // 8x framesPerMs
            var f = new List<byte> { 0x51, 0x8B, 0x0D }; f.AddRange(u(logic));         // push ecx / mov ecx,[TheGameLogic]
            f.AddRange(new byte[] { 0xDB, 0x41, 0x50, 0xA1 }); f.AddRange(u(engineVar)); // fild [ecx+50h] / mov eax,[engine]
            f.AddRange(new byte[] { 0x85, 0xC0, 0x74, 0x03, 0xD8, 0x40, 0x60 });        // fadd [eax+60h] if engine set
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(spf));                  // fmul [secondsPerFrame]
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(k1000));                // fmul [1000.0]
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(fpmFineVa));            // fmul [0.24]
            f.AddRange(new byte[] { 0x83, 0xEC, 0x08, 0xD9, 0x3C, 0x24, 0x0F, 0xB7, 0x04, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00,
                                    0x89, 0x44, 0x24, 0x04, 0xD9, 0x6C, 0x24, 0x04, 0xDB, 0x5C, 0x24, 0x04, 0xD9, 0x2C, 0x24,
                                    0x8B, 0x44, 0x24, 0x04, 0x83, 0xC4, 0x08, 0x59, 0xC3 });   // truncate / pop ecx / ret
            Write(proc, stubVa, f.ToArray());
            // start + duration in the same units
            foreach (uint op in new[] { block + 0x2F, block + 0x6D, block + 0xAD })
            {
                uint o;
                Protect(proc, (IntPtr)op, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out o);
                Write(proc, op, u(fpmFineVa));
                Protect(proc, (IntPtr)op, (UIntPtr)4, o, out o);
            }
        }
        var s = new List<byte> { 0x51, 0x8B, 0x0D }; s.AddRange(u(logic));   // fallback, whole ticks
        s.AddRange(new byte[] { 0xFF, 0x71, 0x50 });                           // push [ecx+50h]
        s.Add(0xE8); s.AddRange(u(conv - (stubVa + (uint)s.Count + 4)));        // frames -> seconds
        s.AddRange(new byte[] { 0xD8, 0x0D }); s.AddRange(u(k1000));            // fmul [1000.0]
        s.AddRange(new byte[] { 0xD8, 0x0D }); s.AddRange(u(fpm));              // fmul [framesPerMs]
        s.AddRange(new byte[] { 0xD8, 0x89, 0xBC, 0x01, 0x00, 0x00 });          // fmul [ecx+1BCh] game speed
        s.AddRange(new byte[] { 0x83, 0xEC, 0x08, 0xD9, 0x3C, 0x24, 0x0F, 0xB7, 0x04, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00,
                                0x89, 0x44, 0x24, 0x04, 0xD9, 0x6C, 0x24, 0x04, 0xDB, 0x5C, 0x24, 0x04, 0xD9, 0x2C, 0x24,
                                0x8B, 0x44, 0x24, 0x04, 0x83, 0xC4, 0x08 });   // truncate
        s.AddRange(new byte[] { 0x59, 0xC3 });                                  // pop ecx / ret
        if (!fine) Write(proc, stubVa, s.ToArray());

        // mov edx,[eax+74h] / fldcw / call edx -> fldcw / call stub
        uint s1 = block + 0xD0;
        var p1 = new List<byte> { 0xD9, 0x6C, 0x24, 0x12, 0xE8 }; p1.AddRange(u(stubVa - (s1 + 9)));
        // mov ecx,[client] / ... / call eax -> call stub
        uint s2 = block + 0xF5;
        var p2 = new List<byte> { 0xE8 }; p2.AddRange(u(stubVa - (s2 + 5))); p2.AddRange(Enumerable.Repeat((byte)0x90, 8));
        foreach (var site in new[] { Tuple.Create(s1, p1.ToArray()), Tuple.Create(s2, p2.ToArray()) })
        {
            uint old;
            if (!Protect(proc, (IntPtr)site.Item1, (UIntPtr)site.Item2.Length, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, site.Item1, site.Item2);
            Protect(proc, (IntPtr)site.Item1, (UIntPtr)site.Item2.Length, old, out old);
        }
    }

    // uprising: a rope/trail renderer (1.1 0x8B7060) locks a vertex buffer and writes into it without checking.
    // at high fps the lock can come back null -> crash writing 0 (#18, desolator killing infantry).
    // if the lock fails, draw nothing this frame and leave the way the "no points" path does
    const string TrailLockPattern = "8B 8E A0 00 00 00 8B 01 8B 50 0C 6A 00 53 FF D2 33 ED 85 FF 8B D8 89 6C 24 54 0F 8E";

    static uint FindTrailLock(byte[] img)
    {
        uint m = FindUnique(img, TrailLockPattern);
        if (m == 0) return 0;
        int o = (int)(m - ImageBase);
        // sub esp,1E4h / push ebx / push esi / mov esi,ecx at the start, and the draw count store at [esi+94h]
        bool prologue = img[o - 0xA6] == 0x81 && img[o - 0xA5] == 0xEC && BitConverter.ToUInt32(img, o - 0xA4) == 0x1E4
                        && img[o - 0xA0] == 0x53 && img[o - 0x9F] == 0x56 && img[o - 0x9E] == 0x8B && img[o - 0x9D] == 0xF1;
        bool count = img[o - 0x30] == 0x89 && img[o - 0x2F] == 0x96 && BitConverter.ToUInt32(img, o - 0x2E) == 0x94;
        return prologue && count ? m + 0x10 : 0;
    }

    static void PatchTrailLock(IntPtr proc, uint site, uint stubVa)
    {
        var s = new Asm(stubVa);
        s.E(0x85, 0xC0); s.J(0x74, "fail");                          // test eax,eax / jz fail
        s.E(0x8B, 0xD8, 0x33, 0xED, 0x85, 0xFF, 0xC3);                // original: mov ebx,eax / xor ebp,ebp / test edi,edi / ret
        s.L("fail");
        s.E(0x83, 0xC4, 0x04);                                        // drop our return address
        s.E(0xC7, 0x86, 0x94, 0, 0, 0, 0, 0, 0, 0);                   // mov dword [esi+94h],0 (nothing to draw)
        s.E(0x5D, 0x5F, 0x5E, 0x5B, 0x81, 0xC4, 0xE4, 0x01, 0, 0, 0xC3);   // pop ebp,edi,esi,ebx / add esp,1E4h / ret
        Write(proc, stubVa, s.Done(0x40));
        var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stubVa - (site + 5))); p.Add(0x90);
        Write(proc, site, p.ToArray());
    }

    // tw/kw: StreamDraw::Stream (flamethrower etc, #21). its update runs every drawn frame and adds a new point
    // each time (points move by real time, frames / fps). the vertex buffer holds what a 30hz stream makes, so at
    // 240 the mesh writer runs off the end of it and crashes. only the add is held to 30hz: the update still runs
    // every frame, so the stream moves as smoothly as before with the same number of points as stock
    const string StreamUpdatePattern = "8B F1 E8 ?? ?? ?? ?? 8B 46 50 89 46 4C 8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 89 46 50 2B 46 4C";

    static uint FindStreamUpdate(byte[] img)
    {
        uint m = FindUnique(img, StreamUpdatePattern);
        if (m == 0) return 0;
        // cmp byte [esi+55h],0 / je / mov ecx,esi / call add point
        int o = (int)(m - ImageBase) + 0x39;
        byte[] want = { 0x80, 0x7E, 0x55, 0x00, 0x74, 0x07, 0x8B, 0xCE, 0xE8 };
        for (int i = 0; i < want.Length; i++) if (img[o + i] != want[i]) return 0;
        return m + 0x39 + 8;
    }

    static void PatchStreamUpdate(IntPtr proc, byte[] img, uint site, uint fpsVa, uint stubVa)
    {
        // add a point only when the 30hz frame moved since the last update: [esi+4Ch] last frame, [esi+50h] now
        var s = new Asm(stubVa);
        s.E(0x51);                                                    // push ecx
        s.E(0x8B, 0x46, 0x50, 0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); s.D(fpsVa);   // eax = now * 30 / fps
        s.E(0x50);                                                    // push eax
        s.E(0x8B, 0x46, 0x4C, 0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); s.D(fpsVa);   // eax = last * 30 / fps
        s.E(0x5A, 0x59, 0x3B, 0xC2); s.J(0x75, "add");               // pop edx / pop ecx / cmp eax,edx / jne add
        s.E(0xC3);
        s.L("add"); s.Rel(0xE9, CallTarget(img, site));               // jmp add point
        Write(proc, stubVa, s.Done(0x40));
        Write(proc, site + 1, BitConverter.GetBytes(stubVa - (site + 5)));
    }

    // seconds turned into frames with the render fps setting (still 30, only the pacing reads get the real fps) and
    // then counted with getFrame, which counts drawn frames: unit flash every fps/2 frames, radar event and marker
    // lifetimes, a 10 s duplicate window, a 30 s repeat. those reads get the real fps so N seconds is N seconds again.
    // only reads tied to getFrame: right after a getFrame call, or a global made from the fps whose one reader sits
    // right after one. fades etc. that count with a 30hz clock keep 30 on purpose
    static List<uint> FindFpsFrameReads(byte[] img, uint render, List<uint> pacing)
    {
        var list = new List<uint>();
        if (render == 0) return list;
        int end = TextEnd(img);
        Func<int, uint> dw = o => BitConverter.ToUInt32(img, o);
        // getFrame: mov ecx,[client] / mov r,[ecx] / call [r+78h] (c&c3), or mov r,[r+74h] / call r (ra3)
        var count = new Dictionary<uint, int>();
        for (int i = 0x1000; i < end - 16; i++)
        {
            if (img[i] != 0x8B || img[i + 1] != 0x0D) continue;
            if (Slot(img, i + 6) == 0) continue;
            uint g = dw(i + 2); int c; count.TryGetValue(g, out c); count[g] = c + 1;
        }
        if (count.Count == 0) return list;
        uint client = count.OrderByDescending(kv => kv.Value).First().Key;
        var callEnds = new HashSet<int>(); var callStarts = new HashSet<int>(); var spans = new List<int[]>();
        for (int i = 0x1000; i < end - 16; i++)
            if (img[i] == 0x8B && img[i + 1] == 0x0D && dw(i + 2) == client) { int e = Slot(img, i + 6); if (e != 0) { callEnds.Add(e); callStarts.Add(i); spans.Add(new[] { i, e }); } }
        Func<int, bool> afterFrame = o => { for (int k = o - 40; k <= o; k++) if (callEnds.Contains(k)) return true; return false; };
        // render reads: mov eax,[r] / mov r32,[r] / fild [r] / cmp r32,[r]. o = operand offset
        Func<int, bool> isRead = o => img[o - 1] == 0xA1 || (img[o - 2] == 0x8B || img[o - 2] == 0xDB || img[o - 2] == 0x3B || img[o - 2] == 0xF7
            || img[o - 3] == 0x0F && img[o - 2] == 0xAF) && (img[o - 1] & 0xC7) == 0x05;   // + mul/div/imul [fps]
        var reads = new List<int>();
        byte[] R = BitConverter.GetBytes(render);
        for (int o = 0x1002; o < end - 4; o++)
            if (img[o] == R[0] && img[o + 1] == R[1] && img[o + 2] == R[2] && img[o + 3] == R[3] && isRead(o)) reads.Add(o);
        var pick = new HashSet<int>();
        // the read and its sign-test partner a few bytes away (fild [fps] / mov eax,[fps] / test / jge / fadd 2^32)
        Action<int> take = o => { foreach (int r in reads) if (Math.Abs(r - o) <= 12) pick.Add(r); };
        foreach (int o in reads) if (afterFrame(o - (img[o - 1] == 0xA1 ? 1 : img[o - 3] == 0x0F ? 3 : 2))) take(o);
        // mov r,[fps] right before the getFrame call, both used together after it (frame % (2 * fps) pulses)
        foreach (int o in reads)
            if (img[o - 2] == 0x8B && (spans.Any(sp => sp[0] < o && o < sp[1]) || Enumerable.Range(o + 4, 5).Any(callStarts.Contains))) take(o);
        // frame budget timers: the constructor stores round(fps * seconds), the update (vtable slot 1) subtracts
        // drawn frames from it. mov [esi],vtable / mov [esi+4],r / mov [esi+8],eax / fild [fps]
        byte[] Rb = BitConverter.GetBytes(render);
        foreach (uint m in FindAll(img, "C7 06 ?? ?? ?? ?? 89 ?? 04 89 46 08 DB 05 " + string.Join(" ", Rb.Select(x => x.ToString("X2")))))
        {
            int mo = (int)(m - ImageBase);
            uint vt = dw(mo + 2);
            if (vt < ImageBase || vt - ImageBase + 8 > img.Length) continue;
            uint upd = dw((int)(vt - ImageBase) + 4);
            if (upd < ImageBase + 0x1000 || upd >= ImageBase + (uint)end) continue;
            int uo = (int)(upd - ImageBase);
            bool frames = false;
            for (int k = uo; k < uo + 0x30 && !frames; k++) frames = callEnds.Contains(k);
            if (frames) take(mo + 14);
        }
        // globals built from the fps: writer mov [g],eax (A3) / mov [g],r32 (89 05..3D) with fps reads shortly before
        var writers = new Dictionary<uint, List<int>>();
        for (int i = 0x1000; i < end - 8; i++)
        {
            int o = img[i] == 0xA3 ? i + 1 : img[i] == 0x89 && (img[i + 1] & 0xC7) == 0x05 ? i + 2 : -1;
            if (o < 0) continue;
            uint g = dw(o);
            if (g == render || g < ImageBase + (uint)end) continue;
            List<int> w; if (!writers.TryGetValue(g, out w)) writers[g] = w = new List<int>(); w.Add(o);
        }
        foreach (var kv in writers)
        {
            // fps reads feeding this writer: up to 0xB0 back, not across a ret that starts another function
            var feed = new List<int>();
            foreach (int wo in kv.Value)
                foreach (int r in reads)
                    if (r < wo && r > wo - 0xB0)
                    {
                        bool cut = false;
                        for (int k = r; k < wo - 1 && !cut; k++) cut = (img[k] == 0xC3 || img[k] == 0xCC) && (img[k + 1] == 0x51 || img[k + 1] == 0x55 || img[k + 1] == 0x56 || img[k + 1] == 0xCC);
                        if (!cut) feed.Add(r);
                    }
            if (feed.Count == 0) continue;
            // every other memory use of g: exactly one, div [g] / cmp r32,[g], right after a getFrame call
            byte[] G = BitConverter.GetBytes(kv.Key);
            var uses = new List<int>();
            for (int o = 0x1002; o < end - 4; o++)
                if (img[o] == G[0] && img[o + 1] == G[1] && img[o + 2] == G[2] && img[o + 3] == G[3] && !kv.Value.Contains(o) && (img[o - 1] & 0xC7) == 0x05 && ModRmOps.Contains(img[o - 2])) uses.Add(o);
            if (uses.Count != 1) continue;
            int u = uses[0];
            bool divOrCmp = img[u - 2] == 0xF7 && (img[u - 1] == 0x35 || img[u - 1] == 0x3D) || img[u - 2] == 0x3B;
            if (!divOrCmp || !afterFrame(u - 2)) continue;
            foreach (int r in feed) pick.Add(r);
        }
        foreach (int o in pick.OrderBy(x => x))
        {
            uint va = ImageBase + (uint)o;
            if (!pacing.Contains(va)) list.Add(va);
        }
        return list;
    }

    // opcodes that take a modrm byte (so 3D in cmp eax,imm32 isn't mistaken for one)
    static readonly HashSet<byte> ModRmOps = new HashSet<byte> { 0x01, 0x03, 0x0B, 0x23, 0x29, 0x2B, 0x33, 0x39, 0x3B, 0x81, 0x83, 0x89, 0x8B, 0x8D, 0xC7, 0xD8, 0xD9, 0xDB, 0xDC, 0xDD, 0xF7, 0xFF };

    // end of a getFrame call starting at i (after mov ecx,[client]): mov r,[ecx] then call [r+78h], or
    // mov r,[r+74h] / call r. 0 if it isn't one
    static int Slot(byte[] img, int i)
    {
        if (img[i] == 0x8B && img[i + 1] >= 0xC0 && (img[i + 2] != 0x8B || (img[i + 3] & 0xC7) == 0x01)) i += 2;   // mov r,r in between
        if (img[i] != 0x8B || (img[i + 1] & 0xC7) != 0x01) return 0;          // mov r,[ecx]
        int r = (img[i + 1] >> 3) & 7;
        for (int k = i + 2; k < i + 12; k++)
        {
            if (img[k] == 0xFF && img[k + 1] == 0x50 + r && img[k + 2] == 0x78) return k + 3;            // call [r+78h]
            if (img[k] == 0x8B && (img[k + 1] & 0xC7) == 0x40 + r && img[k + 2] == 0x74)                    // mov r2,[r+74h]
            {
                int r2 = (img[k + 1] >> 3) & 7;
                for (int j = k + 3; j < k + 18; j++) if (img[j] == 0xFF && img[j + 1] == 0xD0 + r2) return j + 2;
            }
        }
        return 0;
    }

    // blinks keyed off the low bits of getFrame (drawn frames): tw/kw radar blips (frame & 4, ~4 hz) and a marker
    // drawn on odd frames (frame & 1) in all of them. at 240 they turn into a flicker / look solid, so those
    // calls get a 30hz frame
    class Blinks { public byte Slot; public int Len; public uint TimerInit, TimerUpdate; public List<uint> Sites = new List<uint>(), Tint = new List<uint>(); }

    static Blinks FindBlinks(byte[] img)
    {
        var b = new Blinks();
        // c&c3: mov ecx,[client] / mov eax,[ecx] / call [eax+78h] / test al,4 or 1
        foreach (string t in new[] { "A8 04", "A8 01" })
            foreach (uint m in FindAll(img, "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 " + t)) b.Sites.Add(m + 6);
        // tw/kw tint envelope (color flash): set stores frame + attack/peak/decay frames (30 fps frames), install and
        // play compare getFrame with them. all three get the 30hz frame or none
        uint tset = FindUnique(img, "55 8B EC 56 8B F1 8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 8B 4D 08 8B 55 0C 03 C8");
        uint tins = FindUnique(img, "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 39 46 3C 76");
        uint tply = FindUnique(img, "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 83 65 F0 00 8B D8");
        if (tset != 0 && tins != 0 && tply != 0) b.Tint.AddRange(new[] { tset + 12, tins + 6, tply + 6 });
        // tw/kw model timer: init stamps [esi+off] = getFrame, update adds getFrame - stamp to a progress capped at a
        // duration in 30 fps frames. both get the 30hz frame (the init's add esp,10h sits inside the call)
        uint tup = FindUnique(img, "8B 0D ?? ?? ?? ?? 8B 01 FF 50 78 8B D0 8D 8B ?? ?? 00 00 2B 11");
        if (tup != 0)
        {
            ushort off = BitConverter.ToUInt16(img, (int)(tup - ImageBase) + 15);
            string o = (off & 0xFF).ToString("X2") + " " + (off >> 8).ToString("X2");
            uint tin = FindUnique(img, "8B 0D ?? ?? ?? ?? 8B 01 83 C4 10 FF 50 78 89 86 " + o + " 00 00");
            if (tin != 0) { b.TimerInit = tin + 6; b.TimerUpdate = tup + 6; }
        }
        if (b.Sites.Count > 0 || b.Tint.Count > 0 || b.TimerUpdate != 0) { b.Slot = 0x78; b.Len = 5; return b; }
        // ra3: mov ecx,[client] / mov edx,[ecx] / mov eax,[edx+74h] / call eax / test al,1
        foreach (uint m in FindAll(img, "8B 0D ?? ?? ?? ?? 8B 11 8B 42 74 FF D0 A8 01")) b.Sites.Add(m + 6);
        b.Slot = 0x74; b.Len = 7;
        return b;
    }

    static void PatchBlinks(IntPtr proc, Blinks b, uint fpsVa, uint stubVa)
    {
        // eax = getFrame() * 30 / fps
        var s = new Asm(stubVa);
        s.E(0x8B, 0x01, 0xFF, 0x50, b.Slot, 0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); s.D(fpsVa); s.E(0xC3);
        Write(proc, stubVa, s.Done(0x20));
        foreach (uint site in On("blinks") ? b.Sites : new List<uint>())
        {
            var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stubVa - (site + 5)));
            while (p.Count < b.Len) p.Add(0x90);
            Write(proc, site, p.ToArray());
        }
        if (On("modeltimer") && b.TimerUpdate != 0)
        {
            Write(proc, b.TimerUpdate, new byte[] { 0xE8 }.Concat(BitConverter.GetBytes(stubVa - (b.TimerUpdate + 5))).ToArray());
            // mov eax,[ecx] / add esp,10h / call [eax+78h] -> add esp,10h / call stub
            Write(proc, b.TimerInit, new byte[] { 0x83, 0xC4, 0x10, 0xE8 }.Concat(BitConverter.GetBytes(stubVa - (b.TimerInit + 8))).ToArray());
        }
        if (On("tint"))
            foreach (uint site in b.Tint)
            {
                var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stubVa - (site + 5)));
                Write(proc, site, p.ToArray());
            }
    }

    // ra3/uprising: a drawable pulse (sine on getFrame % period, two counters -1 per call) that the drawable update
    // runs once per drawn frame. that call now only goes through on drawn frames that start a new 30hz frame
    // (same answer for every drawable in a frame, no state needed), and the sine reads the 30hz frame.
    // between those frames the pulse keeps its last value, as at 30 fps
    class PulseSite { public uint Fn, Sine, Call, Client; }

    static PulseSite FindPulse(byte[] img)
    {
        uint fn = FindUnique(img, "83 EC 08 56 8B F1 83 86 ?? ?? 00 00 FF 79 0A C7 86 ?? ?? 00 00 00 00 00 00 A1");
        uint sine = FindUnique(img, "8B 0D ?? ?? ?? ?? D9 5C 24 0C 8B 11 8B 42 74 FF D0");
        if (fn == 0 || sine == 0 || sine < fn || sine > fn + 0x100) return null;
        int end = TextEnd(img);
        var calls = new List<uint>();
        for (int i = 0x1000; i < end - 5; i++)
        {
            if (img[i] != 0xE8 || CallTarget(img, ImageBase + (uint)i) != fn) continue;
            // the drawable update: the flash's div [fps/2] a little before
            bool flash = false;
            for (int k = i - 0x100; k < i && !flash; k++) flash = img[k] == 0xF7 && img[k + 1] == 0x35;
            if (flash) calls.Add(ImageBase + (uint)i);
        }
        if (calls.Count != 1) return null;
        return new PulseSite { Fn = fn, Sine = sine + 10, Call = calls[0], Client = BitConverter.ToUInt32(img, (int)(sine - ImageBase) + 2) };
    }

    static void PatchPulse(IntPtr proc, PulseSite p, uint fpsVa, uint stubVa)
    {
        uint frameStub = stubVa + 0x40;
        // eax = getFrame() * 30 / fps (ecx = client)
        var f = new Asm(frameStub);
        f.E(0x8B, 0x01, 0xFF, 0x50, 0x74, 0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); f.D(fpsVa); f.E(0xC3);
        Write(proc, frameStub, f.Done(0x20));
        // gate: run the pulse only if f(now) != f(now - 1)
        var g = new Asm(stubVa);
        g.E(0x51, 0x8B, 0x0D); g.D(p.Client);                       // push ecx / mov ecx,[client]
        g.E(0x8B, 0x01, 0xFF, 0x50, 0x74, 0x8B, 0xC8);              // mov eax,[ecx] / call [eax+74h] / mov ecx,eax
        g.E(0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); g.D(fpsVa);  // eax = now * 30 / fps
        g.E(0x50, 0x8D, 0x41, 0xFF);                                 // push eax / lea eax,[ecx-1]
        g.E(0x6B, 0xC0, 0x1E, 0x33, 0xD2, 0xF7, 0x35); g.D(fpsVa);  // eax = (now - 1) * 30 / fps
        g.E(0x5A, 0x59, 0x3B, 0xC2); g.J(0x75, "run");               // pop edx / pop ecx / cmp / jne run
        g.E(0xC3);
        g.L("run"); g.Rel(0xE9, p.Fn);
        Write(proc, stubVa, g.Done(0x40));
        Write(proc, p.Call + 1, BitConverter.GetBytes(stubVa - (p.Call + 5)));
        // mov edx,[ecx] / mov eax,[edx+74h] / call eax -> call frameStub / nop / nop
        Write(proc, p.Sine, new byte[] { 0xE8 }.Concat(BitConverter.GetBytes(frameStub - (p.Sine + 5))).Concat(new byte[] { 0x90, 0x90 }).ToArray());
    }

    // tw/kw turret bones: the draw code keeps prev/next turret angle and blends them with the engine interp, moving
    // next -> prev on frames where interp is exactly 1.0. the time-based scheduler holds interp at 1.0 for a few
    // frames after the last phase, so the turret shifts several times, snaps to the newest angle and then sits still
    // for the rest of the tick (looks like 15 fps). the turret code now gets its own value: 1.0 on the first drawn
    // frame of each 1.0 run (one shift per tick), after that the real time since then / tick length (< 1)
    // ra3/uprising: same turret code (interp at [engine+60h], also shifts when interp equals a saved value), the load
    // sits between test ebp,ebp and its je, so that stub keeps the flags and eax
    class TurretSite { public uint Site, Engine; public int Len; public byte Off, Slot; public bool Keep; }

    static TurretSite FindTurretInterp(byte[] img)
    {
        // tw/kw: mov eax,[engine] / movss xmm0,[eax+48h] / mov eax,[edi+0Ch] / mov ebx,[eax+140h]
        uint m = FindUnique(img, "A1 ?? ?? ?? ?? F3 0F 10 40 48 8B 47 0C 8B 98 40 01 00 00");
        if (m != 0) return new TurretSite { Site = m, Engine = BitConverter.ToUInt32(img, (int)(m - ImageBase) + 1), Len = 10, Off = 0x48, Slot = 0x78 };
        // ra3: mov eax,[engine] / mov ebp,[ecx+138h] / test ebp,ebp / movss xmm0,[eax+60h] / movss [esp+x],xmm0 / je
        m = FindUnique(img, "A1 ?? ?? ?? ?? 8B A9 38 01 00 00 85 ED F3 0F 10 40 60 F3 0F 11 44 24 ?? 74");
        if (m != 0) return new TurretSite { Site = m + 13, Engine = BitConverter.ToUInt32(img, (int)(m - ImageBase) + 1), Len = 5, Off = 0x60, Slot = 0x74, Keep = true };
        return null;
    }

    // the client global: the one most getFrame calls go through
    static uint FindClient(byte[] img)
    {
        var count = new Dictionary<uint, int>();
        int end = TextEnd(img);
        for (int i = 0x1000; i < end - 24; i++)
        {
            if (img[i] != 0x8B || img[i + 1] != 0x0D || Slot(img, i + 6) == 0) continue;
            uint g = BitConverter.ToUInt32(img, i + 2); int c;
            count.TryGetValue(g, out c); count[g] = c + 1;
        }
        return count.Count == 0 ? 0 : count.OrderByDescending(kv => kv.Value).First().Key;
    }

    static void PatchTurretInterp(IntPtr proc, byte[] img, TurretSite t, SchedSite sched, uint data, uint stubVa)
    {
        uint site = t.Site, engine = t.Engine, client = FindClient(img);
        if (client == 0) return;
        uint gFrame = data, gInOne = data + 4, gShift = data + 5, gTime = data + 8, one = data + 0x10, almost = data + 0x14, tickMs = data + 0x18;
        Write(proc, one, BitConverter.GetBytes(1f));
        Write(proc, almost, BitConverter.GetBytes(0.9999f));
        Write(proc, tickMs, BitConverter.GetBytes(sched.Tick / 3f));      // tick is in 1/3 ms
        Write(proc, gFrame, BitConverter.GetBytes(uint.MaxValue));
        var a = new Asm(stubVa);
        if (t.Keep) a.E(0x9C, 0x50);                                        // pushfd / push eax
        a.E(0x51, 0x52, 0x8B, 0x0D); a.D(client);                          // push ecx / push edx / mov ecx,[client]
        a.E(0x8B, 0x01, 0xFF, 0x50, t.Slot);                                // eax = getFrame()
        a.E(0x3B, 0x05); a.D(gFrame); a.J(0x74, "done");                    // decided this frame already
        a.E(0xA3); a.D(gFrame);
        a.E(0xA1); a.D(engine); a.E(0xF3, 0x0F, 0x10, 0x40, t.Off);         // xmm0 = interp
        a.E(0x0F, 0x2E, 0x05); a.D(one); a.J(0x7A, "notone"); a.J(0x75, "notone");   // ucomiss xmm0,[1.0]
        a.E(0x80, 0x3D); a.D(gInOne); a.E(0x00); a.J(0x75, "noshift");      // already in this 1.0 run
        a.E(0xC6, 0x05); a.D(gInOne); a.E(0x01);
        a.E(0xC6, 0x05); a.D(gShift); a.E(0x01);
        a.Rel(0xE8, sched.TimeFn); a.E(0xA3); a.D(gTime);                  // completion time (ms)
        a.J(0xEB, "done");
        a.L("notone"); a.E(0xC6, 0x05); a.D(gInOne); a.E(0x00);
        a.L("noshift"); a.E(0xC6, 0x05); a.D(gShift); a.E(0x00);
        a.L("done");
        a.E(0x80, 0x3D); a.D(gShift); a.E(0x00); a.J(0x74, "frac");
        a.E(0xF3, 0x0F, 0x10, 0x05); a.D(one); a.J(0xEB, "out");           // movss xmm0,[1.0]
        a.L("frac");
        a.Rel(0xE8, sched.TimeFn); a.E(0x2B, 0x05); a.D(gTime);            // eax = ms since completion
        a.E(0xF3, 0x0F, 0x2A, 0xC0);                                        // cvtsi2ss xmm0,eax
        a.E(0xF3, 0x0F, 0x5E, 0x05); a.D(tickMs);                           // divss
        a.E(0xF3, 0x0F, 0x5D, 0x05); a.D(almost);                           // minss
        a.L("out"); a.E(0x5A, 0x59);                                        // pop edx / pop ecx
        if (t.Keep) a.E(0x58, 0x9D);                                        // pop eax / popfd
        a.E(0xC3);
        Write(proc, stubVa, a.Done(0xB8));
        Write(proc, site, new byte[] { 0xE8 }.Concat(BitConverter.GetBytes(stubVa - (site + 5))).Concat(Enumerable.Repeat((byte)0x90, t.Len - 5)).ToArray());
    }

    // ra3/uprising (#14): knocked over lamp posts etc. ToppleUpdate turns the object once per logic tick, but static
    // map objects have a template flag that copies into drawable+13Eh bit 4 = "don't blend between ticks", so the fall
    // shows at the logic rate. while something is toppling that bit gets cleared and the normal blend takes over
    class ToppleSite { public uint Hook; public int DrawOff, FlagOff; }

    static ToppleSite FindTopple(byte[] img)
    {
        // ToppleUpdate::update: state checks, then cmp [edi+38h],0 / push ebx / mov ebx,[edi-8] (the object)
        uint h = FindUnique(img, "83 EC 4C 57 8B F9 8B 47 2C 85 C0 0F 84 ?? ?? ?? ?? 83 F8 02 0F 84 ?? ?? ?? ?? 83 7F 38 00 53 8B 5F F8");
        // where the template flags are copied into the drawable: gives the flag byte and object->drawable offsets
        uint f = FindUnique(img, "C1 E9 04 02 C9 02 C9 32 8F ?? ?? 00 00 6A 3C 80 E1 04 30 8F ?? ?? 00 00 8B 56 04 8B 8A ?? ?? 00 00 8B 86 ?? ?? 00 00");
        if (h == 0 || f == 0) return null;
        int o = (int)(f - ImageBase);
        return new ToppleSite { Hook = h + 26, FlagOff = BitConverter.ToInt32(img, o + 9), DrawOff = BitConverter.ToInt32(img, o + 35) };
    }

    static void PatchTopple(IntPtr proc, ToppleSite t, uint stubVa)
    {
        var a = new Asm(stubVa);
        a.E(0x50, 0x8B, 0x47, 0xF8, 0x85, 0xC0); a.J(0x74, "skip");          // push eax / mov eax,[edi-8] / test / jz
        a.E(0x8B, 0x80); a.D((uint)t.DrawOff); a.E(0x85, 0xC0); a.J(0x74, "skip");   // mov eax,[eax+draw] / test / jz
        a.E(0x80, 0xA0); a.D((uint)t.FlagOff); a.E(0xFB);                     // and byte [eax+flag],0FBh
        a.L("skip"); a.E(0x58);                                               // pop eax
        a.E(0x83, 0x7F, 0x38, 0x00, 0x53);                                    // original: cmp dword [edi+38h],0 / push ebx
        a.Rel(0xE9, t.Hook + 5);
        Write(proc, stubVa, a.Done(0x40));
        Write(proc, t.Hook, new byte[] { 0xE9 }.Concat(BitConverter.GetBytes(stubVa - (t.Hook + 5))).ToArray());
    }

    // #13: the model draw update starts with "if not marked to animate this frame and it has an animation, return".
    // the mark comes from the animation LOD controller, which only runs for models on screen and near enough to the
    // camera, so off-screen (shadow still visible) and far models animate at the logic rate. the jne that skips the
    // second test becomes jmp: every model updates every frame (costs some cpu). tested 2026-10-05: an off-screen air
    // unit's shadow still moves choppy, so the shadow position comes from somewhere else. off unless offscreenanim=1
    static uint FindAnimGate(byte[] img)
    {
        // tw/kw: mov ebx,ecx / cmp byte [ebx+A8h],0 / jne / cmp dword [ebx+ACh],0 / jne skip
        uint m = FindUnique(img, "8B D9 80 BB ?? ?? 00 00 00 75 0D 83 BB ?? ?? 00 00 00 0F 85");
        if (m != 0) return m + 9;
        // ra3: cmp byte [esi+CCh],0 / push edi / mov edi,[esi+4] / mov [esp+0Ch],edi / jne / cmp dword [esi+D0h],0 / jne skip
        m = FindUnique(img, "80 BE ?? ?? 00 00 00 57 8B 7E 04 89 7C 24 0C 75 ?? 83 BE ?? ?? 00 00 00 0F 85");
        return m != 0 ? m + 15 : 0;
    }

    // ra3/uprising: vehicle and boat sway (#12). calcPhysicsXform steps a spring (pitch/roll, boat wobble) once per
    // drawn frame, guarded by "locomotor+C4h != getFrame()", so at 240 it swings 8x fast. the guard gets a 30hz frame,
    // and the result is kept at the end of the drawable's loco info (grown 70h -> 80h) so the frames in between still
    // get the tilt instead of none
    const string SwayPattern = "8B 0D ?? ?? ?? ?? 8B 01 8B 50 74 55 FF D2 8B E8 39 AE ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? 8B 8F ?? ?? ?? ?? E8 ?? ?? ?? ?? 89 A8 ?? ?? ?? ?? 8B 46 04 8B 48 04 8B 41 ?? 83 C0 FF 83 F8 07 77 ?? FF 24 85";

    class SwaySite { public uint Guard, Skip, Alloc, LocoOff; public List<uint> Calls = new List<uint>(), AllocSites = new List<uint>(); }

    static SwaySite FindSway(byte[] img)
    {
        uint m = FindUnique(img, SwayPattern);
        if (m == 0) return null;
        int o = (int)(m - ImageBase), end = TextEnd(img);
        // push ebx / push edi / mov edi,ecx at the start: then [esp+14h] is the info pointer at the je
        if (!(img[o - 0x31] == 0x53 && img[o - 0x30] == 0x57 && img[o - 0x2F] == 0x8B && img[o - 0x2E] == 0xF9)) return null;
        var s = new SwaySite { Guard = m + 8, Skip = (uint)(m + 28 + BitConverter.ToInt32(img, o + 24)) };
        // the skip label returns bl: pop ebp / pop esi / pop edi / mov al,bl / pop ebx / ret 4
        int k = (int)(s.Skip - ImageBase);
        if (!(img[k] == 0x5D && img[k + 1] == 0x5E && img[k + 2] == 0x5F && img[k + 3] == 0x8A && img[k + 4] == 0xC3)) return null;
        // the case blocks: mov ecx,edi / call sway function
        for (int i = o + 0x45; i < o + 0xB0; i++)
            if (img[i] == 0x8B && img[i + 1] == 0xCF && img[i + 2] == 0xE8) s.Calls.Add(ImageBase + (uint)i + 2);
        if (s.Calls.Count < 3) return null;
        // every sway function starts: cmp [esi+loco],0 / jne / push 70h / call alloc / add esp,4 / ... / call ctor
        uint ctor = 0;
        foreach (uint c in s.Calls)
        {
            uint f = CallTarget(img, c);
            int fo = (int)(f - ImageBase), a = -1;
            int p = -1;   // the push 70h (sometimes a push reg sits between the cmp and the jne)
            for (int i = fo; i < fo + 0x20 && p < 0; i++)
            {
                if (img[i] != 0x83 || img[i + 1] != 0xBE || img[i + 6] != 0) continue;
                int j = i + 7 + (img[i + 7] >= 0x50 && img[i + 7] <= 0x57 ? 1 : 0);
                if (img[j] == 0x75 && img[j + 2] == 0x6A && img[j + 3] == 0x70 && img[j + 4] == 0xE8) { a = i; p = j + 2; }
            }
            if (a < 0) return null;
            uint loco = BitConverter.ToUInt32(img, a + 2), alloc = CallTarget(img, ImageBase + (uint)p + 2), ct = 0;
            for (int i = p + 7; i < p + 23; i++) if (img[i] == 0x8B && img[i + 1] == 0xC8 && img[i + 2] == 0xE8) { ct = CallTarget(img, ImageBase + (uint)i + 2); break; }
            if (ct == 0 || (s.LocoOff != 0 && (loco != s.LocoOff || alloc != s.Alloc || ct != ctor))) return null;
            s.LocoOff = loco; s.Alloc = alloc; ctor = ct;
        }
        // every place that makes a loco info (sway functions + savegame load) has to get the bigger size
        int ctorCalls = 0;
        for (int i = 0x1000; i < end - 5; i++)
        {
            if (img[i] != 0xE8 || CallTarget(img, ImageBase + (uint)i) != ctor) continue;
            ctorCalls++;
            for (int j = i - 4; j > i - 24; j--)
                if (img[j] == 0x6A && img[j + 1] == 0x70 && img[j + 2] == 0xE8 && CallTarget(img, ImageBase + (uint)j + 2) == s.Alloc) { s.AllocSites.Add(ImageBase + (uint)j + 2); break; }
        }
        return ctorCalls == s.AllocSites.Count && ctorCalls >= 3 ? s : null;
    }

    static void PatchSway(IntPtr proc, byte[] img, SwaySite s, uint fpsVa, uint mem)
    {
        Func<uint, byte[]> u = BitConverter.GetBytes;
        uint frameStub = mem, cacheStub = mem + 0x20, allocStub = mem + 0x58, thunks = mem + 0xA0;   // up to +160h
        // eax = ceil(getFrame() * 30 / fps), ecx = client
        var f = new Asm(frameStub);
        f.E(0x8B, 0x01, 0xFF, 0x50, 0x74, 0x6B, 0xC0, 0x1E, 0x8B, 0x15); f.D(fpsVa);
        f.E(0x8D, 0x44, 0x10, 0xFF, 0x33, 0xD2, 0xF7, 0x35); f.D(fpsVa); f.E(0xC3);
        Write(proc, frameStub, f.Done(0x20));
        // already stepped this 30hz frame: hand back the kept result
        var c = new Asm(cacheStub);
        c.E(0x8B, 0x87); c.D(s.LocoOff); c.E(0x85, 0xC0); c.J(0x74, "none");     // mov eax,[edi+loco] / test / jz
        c.E(0x8B, 0x4C, 0x24, 0x14);                                             // mov ecx,[esp+14h] (info)
        for (byte k = 0; k < 16; k += 4) c.E(0x8B, 0x50, (byte)(0x70 + k), 0x89, 0x51, k);   // mov edx,[eax+70h+k] / mov [ecx+k],edx
        c.E(0xB3, 0x01);                                                         // mov bl,1
        c.L("none"); c.Rel(0xE9, s.Skip);
        Write(proc, cacheStub, c.Done(0x38));
        // alloc(70h) -> alloc(80h), our 16 bytes start zeroed (caller still does add esp,4)
        var a = new Asm(allocStub);
        a.E(0x68); a.D(0x80); a.Rel(0xE8, s.Alloc); a.E(0x83, 0xC4, 0x04);     // push 80h / call alloc / add esp,4
        a.E(0x85, 0xC0); a.J(0x74, "null");
        for (byte k = 0; k < 16; k += 4) a.E(0xC7, 0x40, (byte)(0x70 + k), 0, 0, 0, 0);   // mov dword [eax+70h+k],0
        a.L("null"); a.E(0xC3);
        Write(proc, allocStub, a.Done(0x40));
        // sway function, then keep its result: push ebx / mov ebx,ecx / push info / push loco / call / copy / pop ebx / ret 8
        var done = new Dictionary<uint, uint>();
        foreach (uint site in s.Calls)
        {
            uint target = CallTarget(img, site), t;
            if (!done.TryGetValue(target, out t))
            {
                t = thunks + (uint)done.Count * 0x40;
                var th = new Asm(t);
                th.E(0x53, 0x8B, 0xD9, 0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C);
                th.Rel(0xE8, target);
                th.E(0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x8B); th.D(s.LocoOff); th.E(0x85, 0xC9); th.J(0x74, "out");
                for (byte k = 0; k < 16; k += 4) th.E(0x8B, 0x50, k, 0x89, 0x51, (byte)(0x70 + k));   // mov edx,[eax+k] / mov [ecx+70h+k],edx
                th.L("out"); th.E(0x5B, 0xC2, 0x08, 0x00);
                Write(proc, t, th.Done(0x40));
                done[target] = t;
            }
            Write(proc, site + 1, u(t - (site + 5)));
        }
        foreach (uint site in s.AllocSites) Write(proc, site + 1, u(allocStub - (site + 5)));
        // mov edx,[eax+74h] / push ebp / call edx -> push ebp / call frame stub
        var g = new List<byte> { 0x55, 0xE8 }; g.AddRange(u(frameStub - (s.Guard + 6)));
        Write(proc, s.Guard, g.ToArray());
        // je skip -> je cache stub
        Write(proc, s.Guard + 16, u(cacheStub - (s.Guard + 20)));
    }

    // ---- bfme2 ----
    // render 30 / logic 5 next to each other like c&c3 (15/30), 6 phases per tick, same scheduler design.
    // differences: compiled with cdq/idiv, and the frame limiter waits 1000 / (engine max fps [engine+0Ch] * net scale)
    // instead of using the render fps global
    // mov eax,1000 / cdq / idiv [render_fps] / mov [frame_ms],eax   (w3d ms per client frame)
    static readonly int[] BfmeFrameMsSig = { 0xB8, 0xE8, 0x03, 0x00, 0x00, 0x99, 0xF7, 0x3D, -1, -1, -1, -1, 0xA3 };
    // call [timeGetTime] / fild [esi+0Ch] / mov edi,eax / fmul [netscale] / fdivr [1000.0] / call _ftol
    const string BfmeLimiterPattern = "FF 15 ?? ?? ?? ?? DB 46 0C 8B F8 D8 0D ?? ?? ?? ?? D8 3D ?? ?? ?? ?? E8";
    // per-frame update: mov ecx,[esi+phase] / cmp ecx,6 / jne / cmp byte [esi+x],0 / je / mov eax,[fps] / cdq / idiv [logic]
    const string BfmeAdvancePattern = "8B 4E ?? 83 F9 06 75 ?? 80 7E ?? 00 74 ?? A1";
    // its interp: [ecx+interp] = [ecx+phase] / [ecx+framesPerTick], clamped 0..1
    const string BfmeInterpPattern = "F3 0F 2A 49 ?? F3 0F 2A 41 ?? F3 0F 5E C1 0F 57 C9 0F 2F C8 F3 0F 11 41";
    // exit of the per-frame update: mov ecx,[global] / mov eax,[ecx] / call [eax+x] / pop edi / pop esi / pop ebx / leave / ret
    const string BfmeExitPattern = "8B 0D ?? ?? ?? ?? 8B 01 FF 90 ?? 00 00 00 5F 5E 5B C9 C3";
    // dispatch(phase): mov edx,[esi] / push eax / mov ecx,esi / call [edx+x]
    const string BfmeDispatchPattern = "8B 16 50 8B CE FF 92 ?? 00 00 00";

    // game client's frame length setter (w3d ms per drawn frame): cvttss2si eax,[esp+4] / mov [frame_ms],eax / ret 4.
    // it gets set to 1000/30 at runtime, which put animations, water etc back on 30 fps time (8x fast at 240)
    const string BfmeFrameMsSetterPattern = "F3 0F 2C 44 24 04 A3 ?? ?? ?? ?? C2 04 00";
    // W3DView::scrollBy (SCROLL_RESOLUTION 250), same fix as c&c3: scale the step through its vtable slot
    // the spell store (power purchase screen) only takes clicks when its extern "AptSpellStore::InputEnabled" says 1:
    // game mode 6, or the in-game UI's flag byte +16h. above 30 fps that flag is off when the click arrives, so buying
    // powers does nothing (other unlockers have the same bug). getter: ... cmp [logic+110h],6 / je yes / cmp [ui+16h],0 ...
    // -> make the je a jmp, the store always takes input (it can only be opened by the player anyway)
    const string BfmeSpellStorePattern = "33 C0 39 44 24 04 75 ?? 38 44 24 0C 75 ?? 8B 0D ?? ?? ?? ?? 83 B9 ?? ?? 00 00 06 74 ?? 8B 0D ?? ?? ?? ?? 38 41 ?? B8 ?? ?? ?? ?? 74 ?? B8";
    const string BfmeScrollByPattern = "55 8B EC 83 EC 64 A1 ?? ?? ?? ?? 80 B8 C0 00 00 00 00 53 8B D9 74 06 80 7B 44 00 75 09 80 BB 01 25 00 00 00 74 07 33 C0 E9";

    class BfmeSites { public uint Render, Logic, Limiter, TimeIat, FrameMs, FrameMsSetter, ScrollFunc, ScrollSlot, SpellStore, StoreJmp, StoreUpdate; public List<uint> Fps = new List<uint>(); public SchedSite Sched;
        public List<Tuple<uint, byte[]>> Restore = new List<Tuple<uint, byte[]>>(); }

    static BfmeSites FindBfme(byte[] img)
    {
        var hits = Scan(img, BfmeFrameMsSig);
        if (hits.Count != 1) return null;
        var b = new BfmeSites { Render = BitConverter.ToUInt32(img, hits[0] + 8), FrameMs = BitConverter.ToUInt32(img, hits[0] + 13) };
        b.Logic = b.Render - 4;
        Func<uint, int> val = va => { int o = (int)(va - ImageBase); return o > 0 && o + 4 <= img.Length ? BitConverter.ToInt32(img, o) : -1; };
        if (val(b.Render) != 30 || val(b.Logic) != 5) return null;
        byte[] R = BitConverter.GetBytes(b.Render), L = BitConverter.GetBytes(b.Logic);
        Func<int, byte[], bool> at = (i, x) => img[i] == x[0] && img[i + 1] == x[1] && img[i + 2] == x[2] && img[i + 3] == x[3];
        int end = TextEnd(img);
        b.Fps.Add(ImageBase + (uint)hits[0] + 8);   // frame_ms init
        for (int i = 0x1004; i < end - 16; i++)
        {
            if (!at(i, R)) continue;
            // mov eax,[fps] / cdq / idiv [logic] = frames per tick
            if (img[i - 1] == 0xA1 && img[i + 4] == 0x99 && img[i + 5] == 0xF7 && img[i + 6] == 0x3D && at(i + 7, L)) b.Fps.Add(ImageBase + (uint)i);
            // cvtsi2ss xmm,[fps] / cvtsi2ss xmm,[logic] (the same ratio as floats)
            else if (img[i - 4] == 0xF3 && img[i - 3] == 0x0F && img[i - 2] == 0x2A && img[i + 4] == 0xF3 && img[i + 5] == 0x0F && img[i + 6] == 0x2A && at(i + 8, L))
                b.Fps.Add(ImageBase + (uint)i);
            // rotwk 2.02 edits the two phase ratio sites (phase dispatcher: idiv [own 8], tick boundary: mov eax,2 / jmp).
            // that fights the time scheduler (move hitching), so put the stock idiv [logic] back and redirect them like stock
            else if (img[i - 1] == 0xA1 && img[i + 4] == 0x99 && img[i + 5] == 0xF7 && img[i + 6] == 0x3D && !at(i + 7, L)
                     && img[i + 11] == 0x56 && img[i + 12] == 0x6A && img[i + 13] == 0x06)
            { b.Fps.Add(ImageBase + (uint)i); b.Restore.Add(Tuple.Create(ImageBase + (uint)i + 7, L)); }
            else if (img[i - 1] == 0xA1 && img[i + 4] == 0x99 && img[i + 5] == 0xB8 && img[i + 10] == 0xEB && img[i + 12] == 0xC8
                     && img[i + 13] == 0x83 && img[i + 14] == 0xF9 && img[i + 15] == 0x06)
            { b.Fps.Add(ImageBase + (uint)i); b.Restore.Add(Tuple.Create(ImageBase + (uint)i + 5, new byte[] { 0xF7, 0x3D, L[0], L[1], L[2], L[3], 0x8B })); }
        }
        if (b.Fps.Count < 4) return null;
        uint lim = FindUnique(img, BfmeLimiterPattern);
        if (lim == 0) return null;
        b.Limiter = lim + 6; b.TimeIat = BitConverter.ToUInt32(img, (int)(lim + 2 - ImageBase));
        uint set = FindUnique(img, BfmeFrameMsSetterPattern);
        if (set != 0 && BitConverter.ToUInt32(img, (int)(set - ImageBase) + 7) == b.FrameMs) b.FrameMsSetter = set;
        // the spell store's per-frame update (sends SetSpellButtonState to the flash movie, but only when a button's
        // state changed). wrapper: mov ecx,[store] / test ecx,ecx / je ret / jmp update / ret. found as the one whose
        // update calls the function that uses the "SetSpellButtonState" string
        int sbo = IndexOf(img, Encoding.ASCII.GetBytes("SetSpellButtonState\0"));
        if (sbo > 0)
        {
            byte[] sb = BitConverter.GetBytes(ImageBase + (uint)sbo);
            var sbRefs = new List<int>();
            for (int i = 0x1000; i < end - 4; i++) if (at(i, sb)) sbRefs.Add(i);
            var hitsW = new List<int>();
            for (int i = 0x1000; i < end - 16; i++)
            {
                if (img[i] != 0x8B || img[i + 1] != 0x0D || img[i + 6] != 0x85 || img[i + 7] != 0xC9 || img[i + 8] != 0x74 || img[i + 9] != 0x05 || img[i + 10] != 0xE9 || img[i + 15] != 0xC3) continue;
                long t = i + 15L + BitConverter.ToInt32(img, i + 11);
                if (t < 0x1000 || t > end - 0x500) continue;
                for (int k = (int)t; k < t + 0x500; k++)
                {
                    if (img[k] != 0xE8) continue;
                    long ft = k + 5L + BitConverter.ToInt32(img, k + 1);
                    if (sbRefs.Any(r => r >= ft && r < ft + 0x80)) { hitsW.Add(i); break; }
                }
            }
            if (hitsW.Count == 1)
            {
                b.StoreJmp = ImageBase + (uint)hitsW[0] + 10;
                b.StoreUpdate = ImageBase + (uint)(hitsW[0] + 15 + BitConverter.ToInt32(img, hitsW[0] + 11));
            }
        }
        uint store = FindUnique(img, BfmeSpellStorePattern);
        if (store != 0 && img[(int)(store - ImageBase) + 0x1B] == 0x74) b.SpellStore = store + 0x1B;
        b.ScrollFunc = FindUnique(img, BfmeScrollByPattern);
        if (b.ScrollFunc != 0)
            for (int o = TextEnd(img) & ~3; o + 4 <= img.Length; o += 4)
                if (BitConverter.ToUInt32(img, o) == b.ScrollFunc) { if (b.ScrollSlot != 0) { b.ScrollSlot = 0; break; } b.ScrollSlot = ImageBase + (uint)o; }
        // scheduler hooks
        uint adv = FindUnique(img, BfmeAdvancePattern), ip = FindUnique(img, BfmeInterpPattern);
        if (adv != 0 && ip != 0 && at((int)(adv - ImageBase) + 15, R))
        {
            int a = (int)(adv - ImageBase);
            byte phase = img[a + 2];
            if (img[(int)(ip - ImageBase) + 9] == phase)
            {
                uint ex = 0, disp = 0;
                foreach (uint m in FindAll(img, BfmeExitPattern)) if (m > adv && m < adv + 0x180) { ex = m; break; }
                foreach (uint m in FindAll(img, BfmeDispatchPattern)) if (m > adv && m < adv + 0x180) { disp = BitConverter.ToUInt32(img, (int)(m - ImageBase) + 7); break; }
                // the per-phase client call right after the advance: mov ecx,[g] / mov ecx,[ecx+off] / imul ecx,ecx,10 / lea eax,[ecx+eax-1] / push edi / push eax / call fn
                uint pre = FindUnique(img, "8B 0D ?? ?? ?? ?? 8B 49 ?? 6B C9 0A 8D 44 01 FF 57 50 E8");
                if (ex != 0 && disp != 0 && pre > adv && pre < adv + 0x40)
                    b.Sched = new SchedSite { Advance = adv, Exit = ex, Phase = phase, Interp = img[(int)(ip - ImageBase) + 24], Dispatch = disp, Tick = 3000 / 5,
                                              PreGlobal = BitConverter.ToUInt32(img, (int)(pre - ImageBase) + 2), PreOff = img[(int)(pre - ImageBase) + 8], PreFn = CallTarget(img, pre + 18) };
            }
        }
        return b;
    }

    static void ApplyPatchesBfme(IntPtr proc, byte[] img, int fps, BfmeSites b)
    {
        bool sched = b.Sched != null && On("sched");
        found = "found: fps limiter" + (b.Sched != null ? " sched" : "") + (b.FrameMsSetter != 0 ? " animclock" : "") + (b.ScrollSlot != 0 ? " scroll" : "") + (b.SpellStore != 0 ? " spellstore" : "") +
                " | missing:" + (b.Sched == null ? " sched" : "") + (b.FrameMsSetter == 0 ? " animclock" : "") + (b.ScrollSlot == 0 ? " scroll" : "") + (b.SpellStore == 0 ? " spellstore" : "");
        IntPtr mem = Alloc(proc, 4096);
        if (mem == IntPtr.Zero) throw new Exception("couldn't allocate patch memory");
        uint m = (uint)mem;
        Write(proc, m, BitConverter.GetBytes(fps));
        foreach (var r in b.Restore) Write(proc, r.Item1, r.Item2);
        Redirect(proc, b.Fps, m);
        // limiter: fild [esi+0Ch] / mov edi,eax (5 bytes) -> call: fild [fps] / mov edi,eax / ret
        if (On("limiter"))
        {
            var s = new List<byte> { 0xDB, 0x05 }; s.AddRange(BitConverter.GetBytes(m)); s.AddRange(new byte[] { 0x8B, 0xF8, 0xC3 });
            Write(proc, m + 0x300, s.ToArray());
            var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(m + 0x300 - (b.Limiter + 5)));
            Write(proc, b.Limiter, p.ToArray());
        }
        // frame length setter: frame_ms = arg * 30 / fps (follows whatever the game asks for, in our frames)
        if (b.FrameMsSetter != 0 && On("animclock"))
        {
            Write(proc, m + 0x350, BitConverter.GetBytes(30f / fps));
            var s = new Asm(m + 0x320);
            s.E(0xF3, 0x0F, 0x10, 0x44, 0x24, 0x04, 0xF3, 0x0F, 0x59, 0x05); s.D(m + 0x350);   // movss xmm0,[esp+4] / mulss xmm0,[30/fps]
            s.E(0xF3, 0x0F, 0x2C, 0xC0, 0xA3); s.D(b.FrameMs); s.E(0xC2, 0x04, 0x00);           // cvttss2si eax,xmm0 / mov [frame_ms],eax / ret 4
            Write(proc, m + 0x320, s.Done(0x30));
            var p = new List<byte> { 0xE9 }; p.AddRange(BitConverter.GetBytes(m + 0x320 - (b.FrameMsSetter + 5)));
            Write(proc, b.FrameMsSetter, p.ToArray());
        }
        if (b.SpellStore != 0 && On("spellstore") && fps > 30) Write(proc, b.SpellStore, new byte[] { 0xEB });
        // spell store update at most every 33 ms, like stock 30 fps: at 240 the next update came ~4 ms after SetLayout,
        // before the flash movie stepped and built its buttons, so the button states went nowhere and (cached as sent)
        // were never sent again: no buyable buttons. jmp update -> throttle stub
        if (b.StoreJmp != 0 && On("spellstore") && fps > 30)
        {
            uint stub = m + 0xE00, last = m + 0xE80;
            var a = new Asm(stub);
            a.E(0x51);                                   // push ecx (the store)
            a.E(0xFF, 0x15); a.D(b.TimeIat);             // call [timeGetTime]
            a.E(0x8B, 0xD0);                             // mov edx,eax
            a.E(0x2B, 0x05); a.D(last);                  // sub eax,[last]
            a.E(0x83, 0xF8, 0x21); a.J(0x72, "skip");    // cmp eax,33 / jb skip
            a.E(0x89, 0x15); a.D(last);                  // mov [last],edx
            a.E(0x59);                                   // pop ecx
            a.Rel(0xE9, b.StoreUpdate);                  // jmp update
            a.L("skip");
            a.E(0x59, 0xC3);                             // pop ecx / ret
            Write(proc, stub, a.Done(0x40));
            var p = new List<byte> { 0xE9 }; p.AddRange(BitConverter.GetBytes(stub - (b.StoreJmp + 5)));
            Write(proc, b.StoreJmp, p.ToArray());
        }
        if (b.ScrollSlot != 0 && On("scroll") && fps > 30) PatchScrollBy(proc, b.ScrollSlot, b.ScrollFunc, fps, m + 0x4F0, m + 0x4F8, m + 0x3C0);
        if (sched)
        {
            // its clock is timeGetTime straight from the import table: jmp [iat]
            var t = new List<byte> { 0xFF, 0x25 }; t.AddRange(BitConverter.GetBytes(b.TimeIat));
            Write(proc, m + 0xF80, t.ToArray());
            b.Sched.TimeFn = m + 0xF80;
            PatchScheduler(proc, img, b.Sched, m, m);
        }
        FlushCode(proc);
        Func<IEnumerable<uint>, string> hex = l => string.Join(" ", l.Where(a => a != 0).Select(a => "0x" + a.ToString("X")));
        patched = "patch memory 0x" + m.ToString("X") + "-0x" + (m + 0xFFF).ToString("X") + "\r\nsites: fps " + hex(b.Fps) + " | limiter " + hex(new[] { b.Limiter }) + " | animclock " + hex(new[] { b.FrameMsSetter }) + " | scroll " + hex(new[] { b.ScrollSlot }) + " | spellstore " + hex(new[] { b.SpellStore, b.StoreJmp }) +
            (b.Sched != null ? " | sched " + hex(new[] { b.Sched.Advance, b.Sched.Exit }) + string.Format(" phase {0:X} interp {1:X} dispatch {2:X}", b.Sched.Phase, b.Sched.Interp, b.Sched.Dispatch) : "") +
            (b.Restore.Count > 0 ? " | restored stock code " + hex(b.Restore.Select(r => r.Item1)) : "");
    }

    static int IndexOf(byte[] img, byte[] what)
    {
        for (int i = 0; i + what.Length <= img.Length; i++)
        {
            int k = 0;
            while (k < what.Length && img[i + k] == what[k]) k++;
            if (k == what.Length) return i;
        }
        return -1;
    }

    // end of .text (img is mapped, see MapImage)
    static int TextEnd(byte[] img)
    {
        int pe = BitConverter.ToInt32(img, 0x3C);
        int sec = pe + 24 + BitConverter.ToUInt16(img, pe + 20);
        return (int)(BitConverter.ToUInt32(img, sec + 12) + BitConverter.ToUInt32(img, sec + 16));
    }

    // exe file -> memory layout, so offset == va - ImageBase everywhere.
    // most builds are already laid out that way, bfme2 1.06 isn't (.text at file 0x600, va 0x1000)
    static byte[] MapImage(byte[] file)
    {
        int pe = BitConverter.ToInt32(file, 0x3C), n = BitConverter.ToUInt16(file, pe + 6), opt = pe + 24;
        int secs = opt + BitConverter.ToUInt16(file, pe + 20);
        var img = new byte[Math.Max(BitConverter.ToInt32(file, opt + 56), file.Length)];
        Array.Copy(file, img, BitConverter.ToInt32(file, opt + 60));   // headers
        for (int i = 0; i < n; i++)
        {
            int s = secs + i * 40;
            int va = BitConverter.ToInt32(file, s + 12), raw = BitConverter.ToInt32(file, s + 20), size = BitConverter.ToInt32(file, s + 16);
            size = Math.Min(size, Math.Min(file.Length - raw, img.Length - va));
            if (size > 0) Array.Copy(file, raw, img, va, size);
        }
        return img;
    }

    // particle manager update: sim step (fixed 30hz, no delta) then rebuilds draw buckets.
    // sim too often = smoke goes white. only throttle the sim, skipping the rebuild crashes
    //   83 EC 08 53 55 56 57   sub esp,8 / push ebx,ebp,esi,edi
    //   8B F9 89 7C 24 14      mov edi,ecx / mov [esp+14h],edi
    //   E8 <sim>               call simulate          <- patched
    //   C7 87 84 00 00 00 00 00 00 00   mov dword [edi+84h],0
    static readonly int[] ParticleUpdateSig = {
        0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x57, 0x8B, 0xF9, 0x89, 0x7C, 0x24, 0x14,
        0xE8, -1, -1, -1, -1, 0xC7, 0x87, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    static uint FindParticleSim(byte[] img, out uint sim)
    {
        var hits = Scan(img, ParticleUpdateSig);
        sim = 0;
        if (hits.Count != 1) return 0;
        uint site = ImageBase + (uint)hits[0] + 13;
        sim = site + 5 + BitConverter.ToUInt32(img, hits[0] + 14);
        return site;
    }

    // acc += 30; if (acc >= fps) { acc -= fps; jmp simulate } else ret
    static void ThrottleParticles(IntPtr proc, uint site, uint sim, uint fpsVa, uint accVa, uint stubVa)
    {
        var s = new List<byte>();
        Action<byte[]> e = b => s.AddRange(b);
        Func<uint, byte[]> u = BitConverter.GetBytes;
        e(new byte[] { 0xA1 }); e(u(accVa));                       // mov eax,[acc]
        e(new byte[] { 0x83, 0xC0, 0x1E });                        // add eax,30
        e(new byte[] { 0x3B, 0x05 }); e(u(fpsVa));                 // cmp eax,[fps]
        e(new byte[] { 0x7C, 0x10 });                              // jl skip
        e(new byte[] { 0x2B, 0x05 }); e(u(fpsVa));                 // sub eax,[fps]
        e(new byte[] { 0xA3 }); e(u(accVa));                       // mov [acc],eax
        uint jmpAt = stubVa + (uint)s.Count;
        e(new byte[] { 0xE9 }); e(u(sim - (jmpAt + 5)));           // jmp simulate
        e(new byte[] { 0xA3 }); e(u(accVa)); e(new byte[] { 0xC3 }); // skip: mov [acc],eax / ret
        Write(proc, stubVa, s.ToArray());

        var call = new List<byte> { 0xE8 };
        call.AddRange(u(stubVa - (site + 5)));
        uint old;
        if (!Protect(proc, (IntPtr)site, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("couldn't unprotect game memory");
        Write(proc, site, call.ToArray());
        Protect(proc, (IntPtr)site, (UIntPtr)5, old, out old);
    }

    static List<int> Scan(byte[] d, int[] pat)
    {
        var r = new List<int>();
        int end = Math.Min(d.Length, 0x7E0000) - pat.Length; // .text only
        for (int i = 0x1000; i <= end; i++)
        {
            if (d[i] != pat[0]) continue;
            int j = 1;
            while (j < pat.Length && (pat[j] < 0 || d[i + j] == pat[j])) j++;
            if (j == pat.Length) r.Add(i);
        }
        return r;
    }

    // ---- win32 ----

    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_EXECUTE_READWRITE = 0x40;

    // memory helpers. the patch code only goes through these, so it's the same in both builds
    // proc 0 = dll inside the game, plain in-process memory access.
    // otherwise RA3HighFps.exe on linux patching the suspended game from outside (see Launch)
    static IntPtr Alloc(IntPtr proc, int size)
    {
        return proc == IntPtr.Zero ? VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE)
                                   : VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    }
    static bool Protect(IntPtr proc, IntPtr addr, UIntPtr size, uint prot, out uint old)
    {
        return proc == IntPtr.Zero ? VirtualProtect(addr, size, prot, out old) : VirtualProtectEx(proc, addr, size, prot, out old);
    }
    static void FlushCode(IntPtr proc) { FlushInstructionCache(proc == IntPtr.Zero ? GetCurrentProcess() : proc, IntPtr.Zero, UIntPtr.Zero); }
    static byte[] Read(IntPtr proc, uint va, int n)
    {
        var b = new byte[n];
        UIntPtr done;
        if (proc == IntPtr.Zero) Marshal.Copy((IntPtr)va, b, 0, n);
        else if (!ReadProcessMemory(proc, (IntPtr)va, b, (UIntPtr)n, out done)) throw new Exception(string.Format("couldn't read game memory at 0x{0:X}", va));
        return b;
    }
    static void Write(IntPtr proc, uint va, byte[] data)
    {
        uint old;
        UIntPtr done;
        if (!Protect(proc, (IntPtr)va, (UIntPtr)data.Length, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception(string.Format("couldn't write game memory at 0x{0:X}", va));
        if (proc == IntPtr.Zero) Marshal.Copy(data, 0, (IntPtr)va, data.Length);
        else if (!WriteProcessMemory(proc, (IntPtr)va, data, (UIntPtr)data.Length, out done))
            throw new Exception(string.Format("couldn't write game memory at 0x{0:X}", va));
        Protect(proc, (IntPtr)va, (UIntPtr)data.Length, old, out old);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
}

// for the drop-in dll (ExecuteInDefaultAppDomain wants static int Method(string))
public static class DllEntry
{
    public static int Run(string dir) { return Program.InProcess(dir); }
    // RA3HighFps.exe calls this by reflection (linux)
    public static int Launch(IntPtr proc, string exe, string dir) { return Program.Launch(proc, exe, dir); }
}
