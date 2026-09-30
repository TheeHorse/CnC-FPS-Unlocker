// C&C FPS Unlocker - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/CnC-FPS-Unlocker
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
        string ini = Path.Combine(dir, "RA3HighFps.ini");
        byte[] img = null;
        try
        {
            int fps = ReadIniFps(ini, 120);
            if (fps < 30 || fps > 240 || fps % 15 != 0) fps = 120;
            float zoom; if (!float.TryParse(ReadIni(ini, "zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoom) || zoom < 1f || zoom > 3f) zoom = 1f;
            int hz = MonitorHz();
            if (hz >= 30 && fps > hz / 15 * 15) fps = Math.Max(30, hz / 15 * 15);
            img = File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName);
            // already patched (old v1.6 launcher still set as the steam launch option)
            uint render = FindRenderFps(img);
            List<uint> pacing = FindPacingSites(img, render, render - 4);
            if (pacing.Count > 0 && BitConverter.ToUInt32(Read(IntPtr.Zero, pacing[0], 4), 0) != BitConverter.ToUInt32(img, (int)(pacing[0] - ImageBase)))
                return 2;
            skip = (ReadIni(ini, "skip") ?? "").ToLowerInvariant();
            ApplyPatches(IntPtr.Zero, img, fps, zoom, true, null, false);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), DateTime.Now + "  drop-in DLL, fps=" + fps + ", zoom=" + zoom +
                (skip != "" ? ", skip=" + skip : "") + ", exe=" + Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName) + "\r\n" + found + "\r\n");
            return 1;
        }
        catch (Exception e)
        {
            string report = "";
            try { if (img != null) report = BuildReport(img); } catch (Exception re) { report = "report failed: " + re.Message; }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), DateTime.Now + "  drop-in DLL failed: " + e + "\r\n\r\n" + report); } catch { }
            return 0;
        }
    }

    // for unknown builds: exe info, sections, and the bytes around every render/logic fps read
    static string BuildReport(byte[] img)
    {
        var sb = new StringBuilder();
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        int pe = BitConverter.ToInt32(img, 0x3C), n = BitConverter.ToUInt16(img, pe + 6), opt = pe + 24;
        int secs = opt + BitConverter.ToUInt16(img, pe + 20);
        sb.AppendFormat("exe {0}\r\nsize {1}  timestamp 0x{2:X8}  entry 0x{3:X}\r\n", exe, img.Length,
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
    static string skip = "", found = "";
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
        // for the log: which fixes this exe has, so reports from unknown builds say what's missing
        var have = new[] {
            new { n = "sched", ok = schedSite != null }, new { n = "scroll", ok = scrollSlot != 0 }, new { n = "camerakeys", ok = held != null },
            new { n = "interp", ok = interpWindow.Count > 0 }, new { n = "limiter", ok = limiter != 0 }, new { n = "construction", ok = unpack != 0 },
            new { n = "anim2d", ok = anim2d.Count > 0 }, new { n = "models", ok = modelStep != 0 }, new { n = "particles", ok = pfxSite != 0 },
            new { n = "fades", ok = fades.Count > 0 }, new { n = "zoom", ok = zoomSite != 0 } };
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
        if (On("models") && modelStep != 0)   // 1/fps instead of 1/30
        {
            Write(proc, (uint)mem + 0x10, BitConverter.GetBytes(1f / fps));
            Redirect(proc, new List<uint> { modelStep }, (uint)mem + 0x10);
        }
        if (On("particles") && throttlePfx && pfxSite != 0)
            ThrottleParticles(proc, pfxSite, pfxSim, (uint)mem, (uint)mem + 8, (uint)mem + 0x40);
        FlushCode(proc);
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
            if (set || upd) list.Add(ImageBase + (uint)i);
        }
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

    class HeldCamera { public uint ZoomIn, ZoomOut, ViewIn, ViewOut, Rotate; }

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
        var pair = FindAll(img, HeldZoomPattern);
        if (pair.Count != 2 || pair[1] != pair[0] + 0x20 || dw(pair[0], 9) != dw(pair[1], 9) || dw(pair[1], 17) != dw(pair[0], 17) + 4)
            return null;   // zoom in, then zoom out 0x20 later: same view, next slot
        var h = new HeldCamera { ZoomIn = pair[0], ZoomOut = pair[1], ViewIn = FindUnique(img, ViewZoomInPattern), ViewOut = FindUnique(img, ViewZoomOutPattern), Rotate = FindUnique(img, HeldRotatePattern) };
        if (h.ViewIn == 0 || h.ViewOut == 0 || h.Rotate == 0) return null;
        // the stock steps, so we know we have the right functions
        if (Math.Abs(FloatAt(img, dw(h.ViewIn, 16)) - 0.96f) > 1e-4 || Math.Abs(FloatAt(img, dw(h.ViewOut, 16)) - 1.05f) > 1e-4) return null;
        if (FloatAt(img, dw(h.ViewIn, 29)) != 1f || FloatAt(img, dw(h.ViewOut, 29)) != 1f) return null;
        // the view's function table has zoomIn / zoomOut next to each other
        bool table = false;
        for (int o = TextEnd(img); o + 8 <= img.Length && !table; o += 4)
            table = BitConverter.ToUInt32(img, o) == h.ViewIn && BitConverter.ToUInt32(img, o + 4) == h.ViewOut;
        return table ? h : null;
    }

    static void PatchHeldCamera(IntPtr proc, byte[] img, HeldCamera h, int fps, uint dataVa, uint stubVa)
    {
        double k = 30.0 / fps;
        var zooms = new[] { new { Beh = h.ZoomIn, Fn = h.ViewIn }, new { Beh = h.ZoomOut, Fn = h.ViewOut } };
        for (int i = 0; i < 2; i++)
        {
            // z = m*z +/- c per frame. same result per second at fps frames: m' = m^(30/fps), c' = c*(1-m')/(1-m)
            byte[] code = img.Skip((int)(zooms[i].Fn - ImageBase)).Take(43).ToArray();
            double m = FloatAt(img, BitConverter.ToUInt32(code, 16)), c = FloatAt(img, BitConverter.ToUInt32(code, 29));
            double m2 = Math.Pow(m, k), c2 = c * (1 - m2) / (1 - m);
            uint mVa = dataVa + (uint)(i * 8), cVa = mVa + 4, stub = stubVa + (uint)(i * 0x40);
            Write(proc, mVa, BitConverter.GetBytes((float)m2));
            Write(proc, cVa, BitConverter.GetBytes((float)c2));
            BitConverter.GetBytes(mVa).CopyTo(code, 16);   // fmul [m']
            BitConverter.GetBytes(cVa).CopyTo(code, 29);   // fsub/fadd [c']
            Write(proc, stub, code);
            // behavior: mov ecx,[view] / (mov eax,[ecx] / mov edx,[eax+slot] / call edx) -> call stub
            uint site = zooms[i].Beh + 13;
            var p = new List<byte> { 0xE8 }; p.AddRange(BitConverter.GetBytes(stub - (site + 5))); p.AddRange(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
            Write(proc, site, p.ToArray());
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
        return list.Count == 2 ? list : new List<uint>();
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
    class SchedSite { public uint Advance, Exit, TimeFn; public byte Phase, Interp; public uint Dispatch; }

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
        uint stubA = mem + 0x800, stubB = mem + 0xA00, table = mem + 0xB0, tickFrame = mem + 0xC8, prevNow = mem + 0xCC;
        foreach (var e in new[] { 0, 33, 67, 100, 133, 167 }.Select((v, i) => new { v, i })) Write(proc, table + (uint)(e.i * 4), BitConverter.GetBytes(e.v));
        uint exitGlobal = BitConverter.ToUInt32(img, (int)(site.Exit + 2 - ImageBase));
        byte ph = site.Phase;
        Write(proc, k200, BitConverter.GetBytes(200f));

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
        a.E(0x81, 0x2D); a.D(t0); a.D(200);                          // network held it back, undo
        a.L("nopend");
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // edx = t = now3 - tickStart
        a.E(0x83, 0xF9, 0x06); a.J(0x72, "mid");
        // phase 6 done, next tick after 66.67ms
        a.E(0x81, 0xFA); a.D(200); a.J(0x7C, "idle");
        a.E(0x81, 0x05); a.D(t0); a.D(200);                          // tickStart += 200
        a.E(0xA3); a.D(tickFrame);
        a.E(0xC7, 0x05); a.D(pend); a.D(1);
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // >2 ticks behind, resync
        a.E(0x81, 0xFA); a.D(400); a.J(0x7E, "pass");
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
        a.E(0x89, 0x56, ph, 0x50, 0x52, 0x8B, 0xCE, 0x8B, 0x16, 0xFF, 0x92); a.D(site.Dispatch);   // [phase]=n / dispatch(n)
        a.E(0x58, 0x8B, 0x4E, ph); a.J(0xEB, "loop");
        a.L("idle");
        a.E(0x59, 0x5A, 0x58, 0x83, 0xC4, 0x04, 0x57); a.Rel(0xE9, site.Exit);
        a.L("pass");                                                 // back to original
        a.E(0x59, 0x5A, 0x58, 0x8B, 0x4E, ph, 0x83, 0xF9, 0x06, 0xC3);
        Write(proc, stubA, a.Done(0x200));

        // stub B: replaces mov ecx,[global] at the exit.
        // interp = (now - tickFrame + frame length) / 200, max 1. goes 1/8 .. 8/8 at 120
        var b = new Asm(stubB);
        b.E(0x50, 0x52);
        b.E(0xA1); b.D(now3); b.E(0x8B, 0xD0, 0x2B, 0x15); b.D(prevNow);   // edx = frame length
        b.E(0xA3); b.D(prevNow);
        b.E(0x85, 0xD2); b.J(0x7D, "dpos"); b.E(0x33, 0xD2);
        b.L("dpos");
        b.E(0x83, 0xFA, 0x64); b.J(0x7E, "dok"); b.E(0xBA); b.D(100);         // clamp 0..100
        b.L("dok");
        b.E(0x2B, 0x05); b.D(tickFrame); b.E(0x03, 0xC2);                    // eax = now - tickFrame + frame length
        b.E(0x85, 0xC0); b.J(0x7D, "b1"); b.E(0x33, 0xC0);
        b.L("b1");
        b.E(0x3D); b.D(200); b.J(0x7E, "b2"); b.E(0xB8); b.D(200);
        b.L("b2");
        b.E(0x50, 0xDB, 0x04, 0x24, 0xD8, 0x35); b.D(k200); b.E(0xD9, 0x5E, site.Interp, 0x58);   // [interp] = eax / 200
        b.E(0x5A, 0x58, 0x8B, 0x0D); b.D(exitGlobal); b.E(0xC3);        Write(proc, stubB, b.Done(0x100));

        foreach (var s in new[] { Tuple.Create(site.Advance, stubA), Tuple.Create(site.Exit, stubB) })
        {
            var p = new List<byte> { 0xE8 }; p.AddRange(u(s.Item2 - (s.Item1 + 5))); p.Add(0x90);
            uint old;
            if (!Protect(proc, (IntPtr)s.Item1, (UIntPtr)6, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("couldn't unprotect game memory");
            Write(proc, s.Item1, p.ToArray());
            Protect(proc, (IntPtr)s.Item1, (UIntPtr)6, old, out old);
        }
    }

    // soviet/empire construction (StructureUnpackUpdate). start + duration are in 30fps frames but
    // "now" is getFrame() = drawn frames, so at 120 it's 4x ahead and builds pop in instantly.
    // use the logic tick through the same conversion instead
    const string UnpackPattern =
        "8B 35 ?? ?? ?? ?? 8B 57 3C D9 86 BC 01 00 00 55 D9 5C 24 14 52 8B CE E8 ?? ?? ?? ?? D8 0D ?? ?? ?? ?? " +
        "D9 7C 24 12 8B CE 0F B7 44 24 12 D8 0D ?? ?? ?? ?? 0D 00 0C 00 00 89 44 24 18 8B 47 40 D8 4C 24 14 50 " +
        "D9 6C 24 1C DF 7C 24 1C 8B 6C 24 1C D9 6C 24 16 E8 ?? ?? ?? ?? D8 0D ?? ?? ?? ?? 8B 4F 38 D9 7C 24 12 " +
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

    // raw offset == rva in these exes
    static int TextEnd(byte[] img)
    {
        int pe = BitConverter.ToInt32(img, 0x3C);
        int sec = pe + 24 + BitConverter.ToUInt16(img, pe + 20);
        return (int)(BitConverter.ToUInt32(img, sec + 20) + BitConverter.ToUInt32(img, sec + 16));
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
    // dll: we're inside the game, so plain in-process memory access
    static IntPtr Alloc(IntPtr proc, int size) { return VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE); }
    static bool Protect(IntPtr proc, IntPtr addr, UIntPtr size, uint prot, out uint old) { return VirtualProtect(addr, size, prot, out old); }
    static void FlushCode(IntPtr proc) { FlushInstructionCache(GetCurrentProcess(), IntPtr.Zero, UIntPtr.Zero); }
    static byte[] Read(IntPtr proc, uint va, int n) { var b = new byte[n]; Marshal.Copy((IntPtr)va, b, 0, n); return b; }
    static void Write(IntPtr proc, uint va, byte[] data)
    {
        uint old;
        if (!VirtualProtect((IntPtr)va, (UIntPtr)data.Length, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception(string.Format("couldn't write game memory at 0x{0:X}", va));
        Marshal.Copy(data, 0, (IntPtr)va, data.Length);
        VirtualProtect((IntPtr)va, (UIntPtr)data.Length, old, out old);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
}

// for the drop-in dll (ExecuteInDefaultAppDomain wants static int Method(string))
public static class DllEntry
{
    public static int Run(string dir) { return Program.InProcess(dir); }
}
