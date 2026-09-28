// RA3 High FPS - by TheeHorse
//
// Runs Red Alert 3 above 30 fps without touching any game files.
// It starts the normal game paused, changes the frame-pacing value in memory,
// then lets it run. Game logic stays at 15 ticks/sec so game speed is normal,
// and since the exe on disk is stock, Tacitus / C&C:Online are fine with it.
//
// Double-click it to open the setup window. Steam runs it with the game's
// command line (launch option: "...\RA3HighFps.exe" %command%) and it just plays.
//
// Extra options (put them before %command%): --fps N, --pfx off, --check <game exe>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const uint ImageBase = 0x400000;

    [STAThread]
    static int Main(string[] argv)
    {
        try
        {
            // Double-clicked with no arguments -> show the setup window instead of launching.
            if (argv.Length == 0)
            {
                SetProcessDPIAware();  // crisp text on scaled displays
                Application.EnableVisualStyles();
                Application.Run(new SetupForm());
                return 0;
            }
            return Run(argv);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "RA3 High FPS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int Run(string[] argv)
    {
        // RA3HighFps.ini next to the exe; --fps overrides it
        int fps = ReadIniFps(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), 120);
        // render_fps is used two ways: as the real frame pacing rate, and as a fixed "30ths of a
        // second" time unit (effects/particles, whose shaders use Time*30). Redirecting the
        // time-unit reads makes particle birth times run fps/30 ahead of the shader clock, so
        // new particles get a huge negative age and blow up into white/colored sheets.
        // Default: every core site except 5, the static init of framesPerMs = fps*0.001, which
        // the particle code uses to stamp birth times. (Pacing-only "1,2,7" leaves another
        // limiter at 30 fps while the main loop expects 8 frames per tick: ~4x slow game.)
        string check = null, extraSpec = "none", coreSpec = "0-4,6,7";
        int logicOverride = 0;  // experimental: overwrite logic_fps (stock 15), as the 2009 60fps mod did
        string msSpec = null;   // experimental: effect-duration readers of frame_ms see 33 ms instead
        // Run only the particle simulation step at its native 30 Hz (fixes white smoke blowout).
        bool throttlePfx = true;
        string dtSpec = null, f30Spec = null;  // experimental: readers of 1/30f and 30.0f see 1/fps and fps
        var pass = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--fps" && i + 1 < argv.Length) fps = int.Parse(argv[++i]);
            else if (argv[i] == "--check" && i + 1 < argv.Length) check = argv[++i];
            else if (argv[i] == "--extra" && i + 1 < argv.Length) extraSpec = argv[++i];
            else if (argv[i] == "--core" && i + 1 < argv.Length) coreSpec = argv[++i];
            else if (argv[i] == "--logic" && i + 1 < argv.Length) logicOverride = int.Parse(argv[++i]);
            else if (argv[i] == "--msfix" && i + 1 < argv.Length) msSpec = argv[++i];
            else if (argv[i] == "--pfx" && i + 1 < argv.Length) throttlePfx = argv[++i] != "off";
            else if (argv[i] == "--dt30" && i + 1 < argv.Length) dtSpec = argv[++i];
            // apitrace's d3d9.dll wrapper (when present in Data\) writes its trace here.
            else if (argv[i] == "--trace" && i + 1 < argv.Length) Environment.SetEnvironmentVariable("TRACE_FILE", argv[++i]);
            else if (argv[i] == "--f30" && i + 1 < argv.Length) f30Spec = argv[++i];
            else if (argv[i] == "--play") continue;  // from the setup window's Play button
            // Launched from Steam as `RA3HighFps.exe %command%`: skip Steam's own RA3.exe path.
            else if (argv[i].EndsWith("RA3.exe", StringComparison.OrdinalIgnoreCase)) continue;
            else pass.Add(argv[i].Contains(" ") ? "\"" + argv[i] + "\"" : argv[i]);
        }
        // Logic runs at 15 ticks/s, so each tick must span a whole number of frames.
        if (fps < 30 || fps > 240 || fps % 15 != 0)
            throw new Exception("fps must be a multiple of 15 between 30 and 240 (e.g. 60, 90, 120, 135, 165, 240).");

        if (check != null)
        {
            // Dry run against a given executable: report what would be patched.
            byte[] c = File.ReadAllBytes(check);
            uint cr = FindRenderFps(c);
            var cs = FindSites(c, cr, cr - 4);
            var cx = FindExtraSites(c, cr, cr - 4, cs);
            string report = string.Format("render_fps @0x{0:X} = {1}, logic_fps = {2}\nsites: {3}\nextra ({4}):\n{5}\n", cr,
                BitConverter.ToInt32(c, (int)(cr - ImageBase)), BitConverter.ToInt32(c, (int)(cr - 4 - ImageBase)),
                string.Join(", ", cs.Select(s => "0x" + s.ToString("X"))), cx.Count,
                string.Join("\n", cx.Select((s, k) => k + ": 0x" + s.ToString("X") + "  " + Hex(c, (int)(s - ImageBase) - 3, 12))));
            var cm = FindFrameMsReaders(c, BitConverter.ToUInt32(c, Scan(c, FrameMsSig)[0] + 14));
            uint pm, ps = FindParticleSim(c, out pm);
            report += string.Format("particle sim call @0x{0:X} (target 0x{1:X})\n", ps, pm);
            foreach (var kv in new[] { Tuple.Create("1/30f", 0x3D088889u), Tuple.Create("30.0f", 0x41F00000u) })
            {
                var r = FindConstReaders(c, kv.Item2);
                report += string.Format("{0} readers ({1}):\n{2}\n", kv.Item1, r.Count,
                    string.Join("\n", r.Select((s, k) => k + ": 0x" + s.ToString("X") + "  " + Hex(c, (int)(s - ImageBase) - 4, 12))));
            }
            report += string.Format("frame_ms readers ({0}):\n{1}\n", cm.Count,
                string.Join("\n", cm.Select((s, k) => k + ": 0x" + s.ToString("X") + "  " + Hex(c, (int)(s - ImageBase) - 3, 14))));
            File.WriteAllText(check + ".hfr-check.txt", report);
            return 0;
        }

        string game = FindGame();
        string sku = LatestSkuDef(game);
        string exe = Path.Combine(game, SetExe(sku));

        byte[] img = File.ReadAllBytes(exe);
        uint render = FindRenderFps(img);
        uint logic = render - 4;
        List<uint> allCore = FindSites(img, render, logic);
        List<uint> sites = SelectExtra(allCore, coreSpec);
        sites.AddRange(SelectExtra(FindExtraSites(img, render, logic, allCore), extraSpec));
        uint frameMs = BitConverter.ToUInt32(img, Scan(img, FrameMsSig)[0] + 14);
        List<uint> msSites = SelectExtra(FindFrameMsReaders(img, frameMs), msSpec);
        uint pfxSim, pfxSite = FindParticleSim(img, out pfxSim);
        List<uint> dtSites = SelectExtra(FindConstReaders(img, 0x3D088889), dtSpec);   // 1/30f
        List<uint> f30Sites = SelectExtra(FindConstReaders(img, 0x41F00000), f30Spec); // 30.0f

        string cmd = "\"" + exe + "\" -config \"" + sku + "\"" + (pass.Count > 0 ? " " + string.Join(" ", pass) : "");
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
        PROCESS_INFORMATION pi;
        if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, game, ref si, out pi))
            throw new Exception("Could not start " + exe + " (error " + Marshal.GetLastWin32Error() + ").");

        try
        {
            // Layout: +0 render fps, +4 frame_ms override, +8 particle accumulator, +40h stub code.
            IntPtr mem = VirtualAllocEx(pi.hProcess, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (mem == IntPtr.Zero) throw new Exception("VirtualAllocEx failed.");
            Write(pi.hProcess, (uint)mem, BitConverter.GetBytes(fps));
            Redirect(pi.hProcess, sites, (uint)mem);
            if (msSites.Count > 0)
            {
                Write(pi.hProcess, (uint)mem + 4, BitConverter.GetBytes(1000 / 30));
                Redirect(pi.hProcess, msSites, (uint)mem + 4);
            }
            // +10h: 1/fps, +14h: fps as floats
            Write(pi.hProcess, (uint)mem + 0x10, BitConverter.GetBytes(1f / fps));
            Write(pi.hProcess, (uint)mem + 0x14, BitConverter.GetBytes((float)fps));
            if (dtSites.Count > 0) Redirect(pi.hProcess, dtSites, (uint)mem + 0x10);
            if (f30Sites.Count > 0) Redirect(pi.hProcess, f30Sites, (uint)mem + 0x14);
            if (throttlePfx && pfxSite != 0)
                ThrottleParticles(pi.hProcess, pfxSite, pfxSim, (uint)mem, (uint)mem + 8, (uint)mem + 0x40);
            if (logicOverride > 0)
                Write(pi.hProcess, logic, BitConverter.GetBytes(logicOverride));  // .data, already writable
            FlushInstructionCache(pi.hProcess, IntPtr.Zero, UIntPtr.Zero);
        }
        catch
        {
            TerminateProcess(pi.hProcess, 1);
            throw;
        }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        // Once the game (and Tacitus) are fully up, log whether the patch survived.
        if (WaitForSingleObject(pi.hProcess, 45000) != 0)
            Diagnose(pi.hProcess, img, sites, fps);
        // Stay alive until the game exits so Steam keeps showing it as running.
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        CloseHandle(pi.hProcess);
        return 0;
    }

    // Writes %TEMP%\RA3HighFps.log: each patched site's live bytes vs. the file,
    // plus the frame limiter's live code and frame_ms value.
    static void Diagnose(IntPtr proc, byte[] img, List<uint> sites, int fps)
    {
        var log = new StringBuilder();
        log.AppendFormat("{0}  fps={1}\r\n", DateTime.Now, fps);
        foreach (uint va in sites)
        {
            uint start = va - 12;
            log.AppendFormat("site 0x{0:X}\r\n  file: {1}\r\n  live: {2}\r\n", va,
                Hex(img, (int)(start - ImageBase), 24), Hex(Read(proc, start, 24), 0, 24));
        }
        var fm = Scan(img, FrameMsSig)[0];
        uint frameMsVa = BitConverter.ToUInt32(img, fm + 14);
        byte[] ms = Read(proc, frameMsVa, 4);
        log.AppendFormat("frame_ms @0x{0:X} = {1}\r\n", frameMsVa, ms == null ? "?" : BitConverter.ToInt32(ms, 0).ToString());
        foreach (ProcessModule m in Process.GetProcessById(GetProcessId(proc)).Modules)
            log.AppendFormat("module {0}\r\n", m.FileName);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), log.ToString());
    }

    static byte[] Read(IntPtr proc, uint va, int n)
    {
        var b = new byte[n]; UIntPtr got;
        return ReadProcessMemory(proc, (IntPtr)va, b, (UIntPtr)n, out got) ? b : null;
    }

    static string Hex(byte[] b, int off, int n)
    {
        if (b == null) return "(unreadable)";
        return string.Join(" ", b.Skip(off).Take(n).Select(x => x.ToString("X2")));
    }

    // Point each 4-byte absolute operand at `target`.
    static void Redirect(IntPtr proc, List<uint> operands, uint target)
    {
        byte[] addr = BitConverter.GetBytes(target);
        foreach (uint va in operands)
        {
            uint old;
            if (!VirtualProtectEx(proc, (IntPtr)va, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, va, addr);
            VirtualProtectEx(proc, (IntPtr)va, (UIntPtr)4, old, out old);
        }
    }

    static void Write(IntPtr proc, uint va, byte[] data)
    {
        UIntPtr n;
        if (!WriteProcessMemory(proc, (IntPtr)va, data, (UIntPtr)data.Length, out n))
            throw new Exception(string.Format("WriteProcessMemory at 0x{0:X} failed.", va));
    }

    // ---- setup window ----------------------------------------------------------

    // Little window you get when you double-click the exe. Picks the fps, copies the
    // exe into the RA3 folder and gives you the Steam launch option to paste.
    class SetupForm : Form
    {
        readonly ComboBox fpsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        string game;

        public SetupForm()
        {
            Text = "RA3 FPS Unlocker Setup";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 280);

            // Header strip
            var banner = new PictureBox { Bounds = new Rectangle(0, 0, 500, 90), SizeMode = PictureBoxSizeMode.StretchImage };
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("banner.jpg"))
                if (s != null) banner.Image = Image.FromStream(s);
            Controls.Add(banner);
            Controls.Add(Line(90));

            try { game = FindGame(); } catch { game = null; }

            Controls.Add(new Label { Text = "Install RA3 FPS Unlocker", AutoSize = true, Location = new Point(20, 106),
                                     Font = new Font(Font, FontStyle.Bold) });
            Controls.Add(new Label { AutoSize = true, Location = new Point(20, 132),
                                     Text = game != null ? "Red Alert 3 found at:" : "Red Alert 3 (Steam version) wasn't found on this PC." });
            if (game != null)
                Controls.Add(new Label { Text = game, AutoEllipsis = true, Bounds = new Rectangle(20, 152, 460, 20),
                                         ForeColor = SystemColors.GrayText });

            Controls.Add(new Label { Text = "Frame rate:", AutoSize = true, Location = new Point(20, 196) });
            for (int f = 30; f <= 240; f += 15) fpsBox.Items.Add(f + " fps");
            int current = game != null ? ReadIniFps(IniPath(game), 0) : 0;
            int pick = current > 0 ? current : Math.Max(30, Math.Min(240, MonitorHz() / 15 * 15));
            fpsBox.SelectedItem = pick + " fps";
            if (fpsBox.SelectedIndex < 0) fpsBox.SelectedItem = "120 fps";
            fpsBox.Bounds = new Rectangle(100, 192, 100, 23);
            Controls.Add(fpsBox);
            Controls.Add(new Label { AutoSize = true, Location = new Point(210, 196), ForeColor = SystemColors.GrayText,
                                     Text = "(your monitor: " + MonitorHz() + " Hz)" });

            Controls.Add(Line(232));
            var install = new Button { Text = "Install", Bounds = new Rectangle(324, 245, 75, 23), Enabled = game != null };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(405, 245, 75, 23), DialogResult = DialogResult.Cancel };
            install.Click += (s, e) => Install();
            Controls.AddRange(new Control[] { install, cancel });
            AcceptButton = install;
            CancelButton = cancel;
            Shown += (s, e) => fpsBox.Focus();
        }

        // Etched separator like the ones in normal Windows setup programs.
        static Label Line(int y)
        {
            return new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, y, 500, 2) };
        }

        int SelectedFps() { return int.Parse(((string)fpsBox.SelectedItem).Split(' ')[0]); }

        // Copies this exe into the game folder (if it isn't already there) and saves the fps.
        void Install()
        {
            try
            {
                string target = Path.Combine(game, "RA3HighFps.exe");
                string self = Application.ExecutablePath;
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, target, true);
                File.WriteAllLines(IniPath(game), new[] { "; RA3 FPS Unlocker settings (multiple of 15, 30-240)", "fps=" + SelectedFps() });
                Clipboard.SetText("\"" + target + "\" %command%");

                MessageBox.Show(this,
                    "RA3 FPS Unlocker is installed (" + SelectedFps() + " fps).\n\n" +
                    "One last step: in Steam, right-click Red Alert 3 > Properties, and paste into " +
                    "Launch Options. The line is already copied to your clipboard.\n\n" +
                    "To change the frame rate later, run this setup again.",
                    "Setup complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't install:\n\n" + ex.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static string IniPath(string game) { return Path.Combine(game, "RA3HighFps.ini"); }

    // Current refresh rate of the main display (falls back to 60).
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
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    // `fps=N` from the given ini, or the fallback if the file/key is missing.
    static int ReadIniFps(string ini, int fallback)
    {
        if (!File.Exists(ini)) return fallback;
        foreach (string line in File.ReadAllLines(ini))
        {
            var m = Regex.Match(line, @"^\s*fps\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return fallback;
    }

    // ---- locating the game -------------------------------------------------

    static string FindGame()
    {
        var libs = new List<string>();
        string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                       ?? @"C:\Program Files (x86)\Steam";
        libs.Add(steam.Replace('/', '\\'));
        string vdf = Path.Combine(libs[0], @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (string l in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string p = Path.Combine(l, @"steamapps\common\Command and Conquer Red Alert 3");
            if (File.Exists(Path.Combine(p, "RA3.exe"))) return p;
        }
        throw new Exception("Steam copy of Red Alert 3 not found.");
    }

    // RA3.exe runs the highest-versioned RA3_<lang>_1.N.SkuDef.
    static string LatestSkuDef(string game)
    {
        var best = Directory.GetFiles(game, "RA3_*_1.*.SkuDef")
            .Select(f => new { f, m = Regex.Match(Path.GetFileName(f), @"_1\.(\d+)\.SkuDef$", RegexOptions.IgnoreCase) })
            .Where(x => x.m.Success)
            .OrderByDescending(x => int.Parse(x.m.Groups[1].Value))
            .FirstOrDefault();
        if (best == null) throw new Exception("No RA3 SkuDef found.");
        return best.f;
    }

    static string SetExe(string sku)
    {
        foreach (string line in File.ReadAllLines(sku))
            if (line.StartsWith("set-exe ", StringComparison.OrdinalIgnoreCase))
                return line.Substring(8).Trim();
        throw new Exception("SkuDef has no set-exe line.");
    }

    // ---- signatures ----------------------------------------------------------
    // -1 is a wildcard; R/L are the 4-byte render_fps / logic_fps addresses.

    // mov eax,1000 / xor edx,edx / div [render_fps] / mov [frame_ms],eax
    static readonly int[] FrameMsSig = { 0xB8, 0xE8, 0x03, 0x00, 0x00, 0x33, 0xD2, 0xF7, 0x35, -1, -1, -1, -1, 0xA3 };

    static uint FindRenderFps(byte[] img)
    {
        var hits = Scan(img, FrameMsSig);
        if (hits.Count != 1) throw new Exception("Unsupported RA3 build (frame limiter not found).");
        return BitConverter.ToUInt32(img, hits[0] + 9);
    }

    static List<uint> FindSites(byte[] img, uint r, uint l)
    {
        int[] R = Bytes(r), L = Bytes(l);
        // name, pattern, offset of the render_fps operand, expected count
        var sigs = new List<Tuple<string, int[], int, int>>
        {
            T("object rate",   Cat(new[] { 0xA1 }, R, new[] { 0x89, 0x86, 0xDC, 0x01, 0x00, 0x00 }), 1, 1),
            T("ratio check",   Cat(new[] { 0xA1 }, R, new[] { 0x33, 0xD2, 0xF7, 0x35 }, L, new[] { 0x56, 0x8B, 0xF0, 0x83, 0xFE, 0x06 }), 1, 1),
            T("main loop",     Cat(new[] { 0xA1 }, R, new[] { 0x33, 0xD2, 0xF7, 0x35 }, L, new[] { 0x8B, 0xF8, 0x83, 0xFF, 0x06 }), 1, 1),
            T("mov esi",       Cat(new[] { 0x8B, 0x35 }, R, new[] { 0x0F, 0x8E, 0xAF, 0x00, 0x00, 0x00, 0xB8, 0x00, 0xFA, 0x00, 0x00 }), 2, 1),
            T("timing fild",   Cat(new[] { 0xCC, 0x51, 0xA1 }, R, new[] { 0xDB, 0x05 }, R, new[] { 0x85, 0xC0, 0x7D, 0x06, 0xD8, 0x05 }), 9, 3),
            T("frame limiter", Cat(FrameMsSig.Take(9).ToArray(), R, new[] { 0xA3 }), 9, 1),
        };
        var sites = new List<uint>();
        foreach (var s in sigs)
        {
            var hits = Scan(img, s.Item2);
            if (hits.Count != s.Item4)
                throw new Exception(string.Format("Unsupported RA3 build ({0}: found {1}, expected {2}).", s.Item1, hits.Count, s.Item4));
            foreach (int h in hits) sites.Add(ImageBase + (uint)(h + s.Item3)); // .text raw offset == RVA
        }
        return sites;
    }

    // Every other read of render_fps, minus the ones that derive the logic rate
    // (mov eax,[R] / shr eax,1 initializers, and [R] / [L] divisions).
    static List<uint> FindExtraSites(byte[] img, uint r, uint l, List<uint> core)
    {
        byte[] R = BitConverter.GetBytes(r), L = BitConverter.GetBytes(l);
        var twoByte = new[] { "8B0D", "8B15", "8B35", "DB05", "F735", "0FAF05", "0FAF0D", "3B05", "3B0D" };
        var list = new List<uint>();
        for (int i = 0x1003; i < 0x7D0000; i++)
        {
            if (img[i] != R[0] || img[i + 1] != R[1] || img[i + 2] != R[2] || img[i + 3] != R[3]) continue;
            uint va = ImageBase + (uint)i;
            if (core.Contains(va)) continue;
            string p2 = img[i - 2].ToString("X2") + img[i - 1].ToString("X2");
            string p3 = img[i - 3].ToString("X2") + p2;
            bool read = img[i - 1] == 0xA1 || twoByte.Contains(p2) || twoByte.Contains(p3);
            if (!read) continue;
            if (img[i + 4] == 0xD1 && img[i + 5] >= 0xE8 && img[i + 5] <= 0xEF) continue;  // R/2 (shr reg,1) -> logic rate
            bool divL = false;
            for (int k = i + 4; k < i + 10; k++)
                if (img[k] == 0xF7 && img[k + 1] == 0x35 && img[k + 2] == L[0] && img[k + 3] == L[1] && img[k + 4] == L[2] && img[k + 5] == L[3]) divL = true;
            if (divL) continue;
            list.Add(va);
        }
        return list;
    }

    // Reads of frame_ms (ms per render frame) outside the frame limiter itself: the
    // ms -> frame conversions used by effect durations. Returns operand VAs.
    static List<uint> FindFrameMsReaders(byte[] img, uint frameMs)
    {
        byte[] F = BitConverter.GetBytes(frameMs);
        var hits = new List<int>();
        for (int i = 0x1003; i < 0x7D0000; i++)
            if (img[i] == F[0] && img[i + 1] == F[1] && img[i + 2] == F[2] && img[i + 3] == F[3]) hits.Add(i);
        // The limiter accumulates a deadline with `add eax,[frame_ms]`; leave that function alone.
        var limiter = hits.Where(i => img[i - 2] == 0x03 && img[i - 1] == 0x05).ToList();
        var list = new List<uint>();
        foreach (int i in hits)
        {
            if (limiter.Any(l => Math.Abs(l - i) < 0x100)) continue;
            string p2 = img[i - 2].ToString("X2") + img[i - 1].ToString("X2");
            string p3 = img[i - 3].ToString("X2") + p2;
            bool read = img[i - 1] == 0xA1 || p2 == "F73D" || p2 == "8B0D" || p2 == "8B15" || p2 == "8B35"
                        || p3 == "0FAF05" || p3 == "0FAF0D";
            if (read) list.Add(ImageBase + (uint)i);
        }
        return list;
    }

    // TheFXParticleSystemManager's per-frame update (vtable +14h) first calls the simulation
    // step, which advances every particle system one fixed 30 Hz step (no time delta), then
    // rebuilds the per-blend-mode draw buckets. At higher render rates the simulation runs too
    // often and additive smoke blows out to white. Only the simulation call may be throttled:
    // skipping the bucket rebuild leaves stale pointers and crashes.
    //   83 EC 08 53 55 56 57   sub esp,8 / push ebx,ebp,esi,edi
    //   8B F9 89 7C 24 14      mov edi,ecx / mov [esp+14h],edi
    //   E8 <sim>               call simulate          <- patched
    //   C7 87 84 00 00 00 00 00 00 00   mov dword [edi+84h],0
    static readonly int[] ParticleUpdateSig = {
        0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x57, 0x8B, 0xF9, 0x89, 0x7C, 0x24, 0x14,
        0xE8, -1, -1, -1, -1, 0xC7, 0x87, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // Returns the VA of the 5-byte `call simulate`, or 0 if not found; sim = its target.
    static uint FindParticleSim(byte[] img, out uint sim)
    {
        var hits = Scan(img, ParticleUpdateSig);
        sim = 0;
        if (hits.Count != 1) return 0;
        uint site = ImageBase + (uint)hits[0] + 13;
        sim = site + 5 + BitConverter.ToUInt32(img, hits[0] + 14);
        return site;
    }

    // Route the simulation call through a stub that only lets it through at 30 Hz:
    //   acc += 30; if (acc >= fps) { acc -= fps; jmp simulate } else ret
    // ecx (this) is left untouched so the tail jump behaves exactly like the original call.
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
        if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, site, call.ToArray());
        VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)5, old, out old);
    }

    // Code that reads a float constant from .rdata via a [disp32] operand. Used to find the
    // hardcoded 30 fps assumptions (1/30 s time steps, 30.0 rates) in render code.
    static List<uint> FindConstReaders(byte[] img, uint bits)
    {
        var addrs = new List<uint>();
        for (int o = 0x7CE000; o + 4 <= Math.Min(img.Length, 0x8B3000); o += 4)
            if (BitConverter.ToUInt32(img, o) == bits) addrs.Add(ImageBase + (uint)o);
        var list = new List<uint>();
        foreach (uint a in addrs)
        {
            byte[] A = BitConverter.GetBytes(a);
            for (int i = 0x1003; i < 0x7D0000; i++)
                if (img[i] == A[0] && img[i + 1] == A[1] && img[i + 2] == A[2] && img[i + 3] == A[3]
                    && (img[i - 1] & 0xC7) == 0x05)   // modrm: mod=00, rm=101 -> [disp32]
                    list.Add(ImageBase + (uint)i);
        }
        list.Sort();
        return list;
    }

    // "all", or comma-separated indices/ranges into the extra list, e.g. "0-20,25".
    static List<uint> SelectExtra(List<uint> extra, string spec)
    {
        if (spec == null || spec == "none") return new List<uint>();
        if (spec == "all") return extra;
        var pick = new List<uint>();
        foreach (string part in spec.Split(','))
        {
            string[] ab = part.Split('-');
            int a = int.Parse(ab[0]), b = ab.Length > 1 ? int.Parse(ab[1]) : a;
            for (int k = a; k <= b && k < extra.Count; k++) pick.Add(extra[k]);
        }
        return pick;
    }

    static Tuple<string, int[], int, int> T(string n, int[] p, int o, int c) { return Tuple.Create(n, p, o, c); }
    static int[] Bytes(uint v) { return BitConverter.GetBytes(v).Select(b => (int)b).ToArray(); }
    static int[] Cat(params int[][] parts) { return parts.SelectMany(p => p).ToArray(); }

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

    // ---- Win32 ---------------------------------------------------------------

    const uint CREATE_SUSPENDED = 0x4, MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000;
    const uint PAGE_READWRITE = 0x04, PAGE_EXECUTE_READWRITE = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                     IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern int GetProcessId(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
}
