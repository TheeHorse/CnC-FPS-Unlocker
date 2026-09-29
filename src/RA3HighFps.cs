// C&C FPS Unlocker - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/CnC-FPS-Unlocker
//
// starts the game suspended, patches it in memory, resumes. nothing on disk changes
// so tacitus / cnc online don't care.
// no args = setup window. steam launch option: "...\RA3HighFps.exe" %command%
// options go before %command%: --fps N, --pfx off, --check <exe>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
            // sitting in a game folder with an ini = just play. otherwise setup
            string here = AppDomain.CurrentDomain.BaseDirectory;
            bool portable = argv.Length == 0 && IsGameFolder(here) && File.Exists(Path.Combine(here, "RA3HighFps.ini"));
            if (portable) return Run(argv);
            if (argv.Length == 0 || (argv.Length == 1 && argv[0] == "--setup"))
            {
                SetProcessDPIAware();
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
        int fps = ReadIniFps(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), 120);
        bool throttlePfx = true;   // particles stay at 30hz like stock
        // experimental stuff, off by default (extra=all, ticks=on)
        string extra = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "extra");
        bool ticks = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "ticks") == "on";
        bool menu = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "menu") == "on";
        float zoom; if (!float.TryParse(ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoom) || zoom < 1f || zoom > 3f) zoom = 1f;
        string check = null, game = null, lang = null;
        int runver = -1;   // -runver 1.12 etc
        var pass = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--fps" && i + 1 < argv.Length) fps = int.Parse(argv[++i]);
            else if (argv[i] == "--check" && i + 1 < argv.Length) check = argv[++i];
            else if (argv[i] == "--pfx" && i + 1 < argv.Length) throttlePfx = argv[++i] != "off";
            else if (argv[i] == "--extra" && i + 1 < argv.Length) extra = argv[++i];
            else if (argv[i] == "--ticks" && i + 1 < argv.Length) ticks = argv[++i] == "on";
            else if (argv[i] == "--game" && i + 1 < argv.Length) game = argv[++i].Trim('"');
            else if (argv[i] == "--lang" && i + 1 < argv.Length) lang = argv[++i].ToLowerInvariant();
            // stock launcher flags. -ui is its launcher window, just drop it
            else if (argv[i].Equals("-runver", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length) runver = int.Parse(argv[++i].Split('.').Last());
            else if (argv[i].Equals("-ui", StringComparison.OrdinalIgnoreCase)) continue;
            else if (argv[i] == "--play") continue;
            else if (argv[i] == "--menu") menu = true;
            else if (argv[i] == "--trace" && i + 1 < argv.Length) Environment.SetEnvironmentVariable("TRACE_FILE", argv[++i]);   // for apitrace
            // first exe in %command% is the game launcher (RA3.exe / CNC3.exe)
            else if (game == null && argv[i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(argv[i]))
                game = Path.GetDirectoryName(Path.GetFullPath(argv[i]));
            else pass.Add(argv[i].Contains(" ") ? "\"" + argv[i] + "\"" : argv[i]);
        }
        // logic is 15 ticks/s so fps has to be a multiple of 15
        if (fps < 30 || fps > 240 || fps % 15 != 0)
            throw new Exception("fps must be a multiple of 15 between 30 and 240 (e.g. 60, 90, 120, 135, 165, 240).");

        if (check != null)
        {
            // dry run, dumps what it would patch
            byte[] c = File.ReadAllBytes(check);
            uint cr = FindRenderFps(c);
            var cs = FindPacingSites(c, cr, cr - 4);
            uint pm, ps = FindParticleSim(c, out pm);
            string report = string.Format("render_fps @0x{0:X} = {1}, logic_fps = {2}\nsites: {3}\nparticle sim call @0x{4:X} (target 0x{5:X})\n",
                cr, BitConverter.ToInt32(c, (int)(cr - ImageBase)), BitConverter.ToInt32(c, (int)(cr - 4 - ImageBase)),
                string.Join(", ", cs.Select(s => "0x" + s.ToString("X"))), ps, pm);
            report += string.Format("model transition step operand @0x{0:X}\n", FindModelTransitionStep(c));
            report += string.Format("structure build-up block @0x{0:X}\n", FindUnpackProgress(c));
            report += "interpolation window checks: " + string.Join(", ", FindInterpWindow(c).Select(s => "0x" + s.ToString("X"))) + "\n";
            uint cno; report += string.Format("zoom max site @0x{0:X}, network object @0x{1:X}\n", FindZoomSite(c, out cno), cno);
            report += "fade frame reads: " + string.Join(", ", FindFadeFrameReads(c).Select(s => "0x" + s.ToString("X"))) + "\n";
            uint sbf, sbs = FindScrollBySlot(c, out sbf);
            report += string.Format("camera scrollBy @0x{0:X} (vtable slot 0x{1:X})\n", sbf, sbs);
            var ss = FindScheduler(c);
            report += "phase ratio sites: " + string.Join(", ", cs.Where(s => IsPhaseRatioSite(c, s)).Select(s => "0x" + s.ToString("X"))) + "\n";
            report += string.Format("tick scheduler: {0}\n", ss != null ? string.Format("advance @0x{0:X}, exit @0x{1:X}, clock @0x{2:X}", ss.Advance, ss.Exit, ss.TimeFn) : "not found");
            bool lr; uint ls = FindLimiterRounding(c, out lr);
            report += string.Format("limiter rounding @0x{0:X} ({1})\n", ls, ls == 0 ? "not found" : lr ? "inline" : "_ftol call");
            report += "anim2d frame reads: " + string.Join(", ", FindAnim2DFrameReads(c).Select(s => "0x" + s.ToString("X"))) + "\n";
            var cx = FindExtraSites(c, cr, cr - 4, cs);
            report += "tick stores: " + string.Join(", ", FindTickStores(c, cr, cr - 4).Select(s => "0x" + s.ToString("X"))) + "\n";
            report += string.Format("extra ({0}):\n{1}\n", cx.Count,
                string.Join("\n", cx.Select((s, k) => k + ": 0x" + s.ToString("X") + "  " + Hex(c, (int)(s - ImageBase) - 3, 12))));
            File.WriteAllText(check + ".hfr-check.txt", report);
            return 0;
        }

        if (game == null && IsGameFolder(AppDomain.CurrentDomain.BaseDirectory)) game = AppDomain.CurrentDomain.BaseDirectory;
        if (game == null) throw new Exception("Run this through Steam (launch option) or from the setup window.");
        if (menu)
        {
            Application.EnableVisualStyles();
            using (var m = new MenuForm(game, lang))
            {
                if (m.ShowDialog() != DialogResult.OK) return 0;
                runver = m.Version;
                if (m.ModConfig != null) { pass.Add("-modConfig"); pass.Add("\"" + m.ModConfig + "\""); }
            }
        }
        // mods say their version in the skudef ("mod-game 1.12"), use it like the stock launcher does
        int mi = pass.FindIndex(a => a.Equals("-modConfig", StringComparison.OrdinalIgnoreCase));
        if (runver < 0 && mi >= 0 && mi + 1 < pass.Count)
        {
            string modCfg = pass[mi + 1].Trim('"');
            if (File.Exists(modCfg))
                foreach (string line in File.ReadAllLines(modCfg))
                {
                    var mg = Regex.Match(line, @"^\s*mod-game\s+1\.(\d+)", RegexOptions.IgnoreCase);
                    if (mg.Success) { runver = int.Parse(mg.Groups[1].Value); break; }
                }
        }
        // vsync caps us at the refresh rate anyway, so don't go over it (144hz -> 135)
        int hz = MonitorHz();
        if (hz >= 30 && fps > hz / 15 * 15) fps = Math.Max(30, hz / 15 * 15);
        string sku = LatestSkuDef(game, lang, runver);
        string exe = Path.Combine(game, SetExe(sku));

        byte[] img = File.ReadAllBytes(exe);
        string cmd = "\"" + exe + "\" -config \"" + sku + "\"" + (pass.Count > 0 ? " " + string.Join(" ", pass) : "");
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
        PROCESS_INFORMATION pi;
        if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, game, ref si, out pi))
            throw new Exception("Could not start " + exe + " (error " + Marshal.GetLastWin32Error() + ").");

        List<uint> sites;
        try
        {
            sites = ApplyPatches(pi.hProcess, img, fps, zoom, throttlePfx, extra, ticks);
        }
        catch
        {
            TerminateProcess(pi.hProcess, 1);
            throw;
        }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        if (WaitForSingleObject(pi.hProcess, 45000) != 0)
            Diagnose(pi.hProcess, img, sites, fps);
        // wait for the game so steam still shows it running
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        CloseHandle(pi.hProcess);
        return 0;
    }

    // drop-in dll version, called from inside the game (dll/proxy.c)
    internal static int InProcess(string dir)
    {
        string ini = Path.Combine(dir, "RA3HighFps.ini");
        try
        {
            int fps = ReadIniFps(ini, 120);
            if (fps < 30 || fps > 240 || fps % 15 != 0) fps = 120;
            float zoom; if (!float.TryParse(ReadIni(ini, "zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoom) || zoom < 1f || zoom > 3f) zoom = 1f;
            int hz = MonitorHz();
            if (hz >= 30 && fps > hz / 15 * 15) fps = Math.Max(30, hz / 15 * 15);
            byte[] img = File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName);
            IntPtr self = Process.GetCurrentProcess().Handle;
            // launcher already got it (steam launch option too)
            uint render = FindRenderFps(img);
            List<uint> pacing = FindPacingSites(img, render, render - 4);
            if (pacing.Count > 0)
            {
                var live = new byte[4]; UIntPtr got;
                ReadProcessMemory(self, (IntPtr)pacing[0], live, (UIntPtr)4, out got);
                if (BitConverter.ToUInt32(live, 0) != BitConverter.ToUInt32(img, (int)(pacing[0] - ImageBase))) return 2;
            }
            ApplyPatches(self, img, fps, zoom, true, null, false);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), DateTime.Now + "  drop-in DLL, fps=" + fps + ", zoom=" + zoom + "\r\n");
            return 1;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "RA3HighFps.log"), DateTime.Now + "  drop-in DLL failed: " + e + "\r\n"); } catch { }
            return 0;
        }
    }

    // proc = suspended game (launcher) or ourselves (dll)
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
        bool sched = schedSite != null;

        // +0 fps, +8 particle accum, +40 stubs
        IntPtr mem = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (mem == IntPtr.Zero) throw new Exception("VirtualAllocEx failed.");
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
        if (zoom > 1f && zoomSite != 0) PatchZoom(proc, zoomSite, netObject, zoom, (uint)mem + 0x98, (uint)mem + 0x4A0);
        if (fades.Count > 0) PatchFadeFrameReads(proc, img, fades, (uint)mem, (uint)mem + 0x440);
        if (scrollSlot != 0 && fps > 30) PatchScrollBy(proc, scrollSlot, scrollFunc, fps, (uint)mem + 0x4F0, (uint)mem + 0x4F8, (uint)mem + 0x3C0);
        if (interpWindow.Count > 0) PatchInterpWindow(proc, interpWindow, fps);
        if (sched) PatchScheduler(proc, img, schedSite, (uint)mem, (uint)mem);
        if (limiter != 0) PatchLimiterRounding(proc, limiter, limiterRA3, (uint)mem + 0x28, (uint)mem + 0x2C, (uint)mem + 0x280);
        if (unpack != 0) PatchUnpack(proc, img, unpack, (uint)mem + 0x200, sched ? (uint)mem + 0x3C : 0, (uint)mem + 0x4E0);   // construction
        if (anim2d.Count > 0) PatchAnim2D(proc, img, anim2d, (uint)mem, (uint)mem + 0x100);
        if (modelStep != 0)   // 1/fps instead of 1/30
        {
            Write(proc, (uint)mem + 0x10, BitConverter.GetBytes(1f / fps));
            Redirect(proc, new List<uint> { modelStep }, (uint)mem + 0x10);
        }
        if (throttlePfx && pfxSite != 0)
            ThrottleParticles(proc, pfxSite, pfxSim, (uint)mem, (uint)mem + 8, (uint)mem + 0x40);
        FlushInstructionCache(proc, IntPtr.Zero, UIntPtr.Zero);
        return sites;
    }

    // %TEMP%\RA3HighFps.log
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

    // ---- setup window ----

    class ExtrasForm : Form
    {
        readonly CheckBox zoomBox = new CheckBox { Text = "Let the camera zoom out further", AutoSize = true };
        readonly ComboBox amountBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        static readonly float[] Amounts = { 1.25f, 1.5f, 1.75f };
        public float Zoom { get { return zoomBox.Checked ? Amounts[Math.Max(0, amountBox.SelectedIndex)] : 1f; } }

        public ExtrasForm(float current)
        {
            Text = "Extras";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowIcon = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Controls.Add(new Label { Text = "Camera", AutoSize = true, Location = new Point(16, 14), Font = new Font(Font, FontStyle.Bold) });
            zoomBox.Location = new Point(16, 38);
            zoomBox.Checked = current > 1f;
            Controls.Add(zoomBox);
            Controls.Add(new Label { Text = "Amount:", AutoSize = true, Location = new Point(34, 68) });
            foreach (float a in Amounts) amountBox.Items.Add(a.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x");
            int idx = Array.FindIndex(Amounts, a => Math.Abs(a - current) < 0.01f);
            amountBox.SelectedIndex = idx >= 0 ? idx : Amounts.Length - 1;
            amountBox.Bounds = new Rectangle(95, 65, 70, 23);
            amountBox.Enabled = zoomBox.Checked;
            zoomBox.CheckedChanged += (s, e) => amountBox.Enabled = zoomBox.Checked;
            Controls.Add(amountBox);
            Controls.Add(new Label { AutoSize = false, Bounds = new Rectangle(34, 96, 330, 48), ForeColor = SystemColors.GrayText,
                                     Text = "Red Alert 3 only. Works in skirmish and campaign; online and LAN games always use the normal zoom." });

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(208, 154, 75, 23) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(289, 154, 75, 23) };
            Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(380, 190);
        }
    }
    class SetupForm : Form
    {
        readonly ComboBox fpsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckedListBox gameList = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        readonly CheckBox menuBox = new CheckBox { Text = "Show a mod && version picker when the game starts", AutoSize = true };
        float zoomValue = 1f;
        List<Tuple<string, string>> games;

        public SetupForm()
        {
            Text = "C&C FPS Unlocker Setup";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;

            var banner = new PictureBox { Bounds = new Rectangle(0, 0, 500, 90), SizeMode = PictureBoxSizeMode.StretchImage };
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("banner.jpg"))
                if (s != null) banner.Image = Image.FromStream(s);
            Controls.Add(banner);
            Controls.Add(Line(90));

            games = FindGames();
            Controls.Add(new Label { Text = "Install C&C FPS Unlocker", AutoSize = true, Location = new Point(20, 106),
                                     Font = new Font(Font, FontStyle.Bold), UseMnemonic = false });
            Controls.Add(new Label { AutoSize = true, Location = new Point(20, 132), UseMnemonic = false,
                                     Text = games.Count > 0 ? "Install for these games:" : "No games found automatically. Use \"Add game folder...\" below." });

            var tips = new ToolTip();
            int listH = Math.Max(3, games.Count) * 20 + 6;
            gameList.Bounds = new Rectangle(20, 152, 460, listH);
            foreach (var g in games) gameList.Items.Add(g.Item1, true);
            Controls.Add(gameList);
            gameList.MouseMove += (s, e) =>
            {
                int i = gameList.IndexFromPoint(e.Location);
                string tip = i >= 0 && i < games.Count ? games[i].Item2 : "";
                if (tips.GetToolTip(gameList) != tip) tips.SetToolTip(gameList, tip);
            };

            int y = gameList.Bottom + 16;
            Controls.Add(new Label { Text = "Frame rate:", AutoSize = true, Location = new Point(20, y + 4) });
            Func<int, string> fpsLabel = f => f + " fps" + (f >= 240 ? " (experimental)" : "");
            for (int f = 30; f <= 240; f += 15) fpsBox.Items.Add(fpsLabel(f));
            int current = games.Select(g => ReadIniFps(IniPath(g.Item2), 0)).FirstOrDefault(v => v > 0);
            int pick = current > 0 ? current : Math.Max(30, Math.Min(240, MonitorHz() / 15 * 15));
            fpsBox.SelectedItem = fpsLabel(pick);
            if (fpsBox.SelectedIndex < 0) fpsBox.SelectedItem = "120 fps";
            fpsBox.Bounds = new Rectangle(100, y, 160, 23);
            Controls.Add(fpsBox);
            Controls.Add(new Label { AutoSize = true, Location = new Point(270, y + 4), ForeColor = SystemColors.GrayText,
                                     Text = "(your monitor: " + MonitorHz() + " Hz)" });

            menuBox.Location = new Point(20, y + 36);
            menuBox.Checked = games.Any(g => ReadIni(IniPath(g.Item2), "menu") == "on");
            Controls.Add(menuBox);
            foreach (var g in games) { float z; if (float.TryParse(ReadIni(IniPath(g.Item2), "zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z) && z > 1f) { zoomValue = z; break; } }
            var extras = new Button { Text = "Extras...", Bounds = new Rectangle(20, y + 62, 90, 23) };
            extras.Click += (s, e) => { using (var x = new ExtrasForm(zoomValue)) if (x.ShowDialog(this) == DialogResult.OK) zoomValue = x.Zoom; };
            Controls.Add(extras);
            y += 100;
            Controls.Add(Line(y));
            var install = new Button { Text = "Install", Bounds = new Rectangle(324, y + 13, 75, 23), Enabled = games.Count > 0 };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(405, y + 13, 75, 23) };
            cancel.Click += (s, e) => Close();   // not a dialog
            install.Click += (s, e) => Install();
            Controls.AddRange(new Control[] { install, cancel });
            AcceptButton = install;
            CancelButton = cancel;
            ClientSize = new Size(500, y + 48);
            Shown += (s, e) => fpsBox.Focus();
        }

        static Label Line(int y)
        {
            return new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, y, 500, 2) };
        }

        int SelectedFps() { return int.Parse(((string)fpsBox.SelectedItem).Split(' ')[0]); }

        void Install()
        {
            var done = new List<Tuple<string, string>>();   // name, launch option
            try
            {
                foreach (int i in gameList.CheckedIndices)
                {
                    string dir = games[i].Item2;
                    string target = Path.Combine(dir, "RA3HighFps.exe");
                    if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                        File.Copy(Application.ExecutablePath, target, true);
                    if (!File.Exists(IniPath(dir))) File.WriteAllLines(IniPath(dir), new[] { "; C&C FPS Unlocker settings (fps: multiple of 15, 30-240; menu: on/off)" });
                    SetIni(IniPath(dir), "fps", SelectedFps().ToString());
                    SetIni(IniPath(dir), "menu", menuBox.Checked ? "on" : "off");
                    SetIni(IniPath(dir), "zoom", zoomValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    done.Add(Tuple.Create(games[i].Item1, "\"" + target + "\" %command%"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't install:\n\n" + ex.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (done.Count == 0) { MessageBox.Show(this, "Tick at least one game.", "Setup"); return; }
            Hide();
            new FinishForm(done, SelectedFps()).ShowDialog();
            Close();
        }
    }

    class FinishForm : Form
    {
        public FinishForm(List<Tuple<string, string>> done, int fps)
        {
            Text = "Setup complete";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;

            Controls.Add(new Label { Text = "Installed at " + fps + " fps. One last step for each game:", AutoSize = true,
                                     Location = new Point(20, 18), Font = new Font(Font, FontStyle.Bold) });
            Controls.Add(new Label { AutoSize = true, Location = new Point(20, 42), UseMnemonic = false,
                                     Text = "In Steam, right-click the game > Properties > Launch Options, and paste its line." });

            int y = 74;
            foreach (var d in done)
            {
                Controls.Add(new Label { Text = d.Item1, AutoSize = true, Location = new Point(20, y), UseMnemonic = false });
                var box = new TextBox { Text = d.Item2, ReadOnly = true, Bounds = new Rectangle(20, y + 20, 380, 23) };
                var copy = new Button { Text = "Copy", Bounds = new Rectangle(405, y + 19, 75, 25) };
                copy.Click += (s, e) => { Clipboard.SetText(box.Text); copy.Text = "Copied"; };
                Controls.AddRange(new Control[] { box, copy });
                y += 56;
            }

            Controls.Add(new Label { AutoSize = true, Location = new Point(20, y + 2), ForeColor = SystemColors.GrayText,
                                     Text = "To change the frame rate later, just run this setup again." });
            y += 30;
            Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, y, 500, 2) });
            var ok = new Button { Text = "Finish", Bounds = new Rectangle(405, y + 13, 75, 23), DialogResult = DialogResult.OK };
            Controls.Add(ok);
            AcceptButton = ok;
            ClientSize = new Size(500, y + 48);
            Shown += (s, e) =>
            {
                foreach (var tb in Controls.OfType<TextBox>()) tb.Select(0, 0);
                ok.Focus();
            };
        }
    }

    // menu=on: mod/version picker, basically the old -ui launcher
    class MenuForm : Form
    {
        readonly ComboBox modBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox verBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly List<string> modPaths = new List<string> { null };   // 0 = no mod
        public string ModConfig { get { return modPaths[modBox.SelectedIndex]; } }
        public int Version { get { string v = (string)verBox.SelectedItem; return v == "Auto" ? -1 : int.Parse(v.Split('.')[1]); } }   // -1 = auto

        public MenuForm(string game, string lang)
        {
            Text = "C&C FPS Unlocker";
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 170);
            string ini = IniPath(game);

            Controls.Add(new Label { Text = GameName(game), AutoSize = true, Location = new Point(20, 16),
                                     Font = new Font(Font, FontStyle.Bold), UseMnemonic = false });

            Controls.Add(new Label { Text = "Mod:", AutoSize = true, Location = new Point(20, 52) });
            modBox.Items.Add("No mod");
            foreach (var m in FindMods(game)) { modBox.Items.Add(m.Item1); modPaths.Add(m.Item2); }
            string lastMod = ReadIni(ini, "menu_mod");
            if (lastMod != null && File.Exists(lastMod) && !modPaths.Contains(lastMod))
            { modBox.Items.Add(Path.GetFileNameWithoutExtension(lastMod)); modPaths.Add(lastMod); }
            modBox.SelectedIndex = Math.Max(0, modPaths.IndexOf(lastMod));
            modBox.Bounds = new Rectangle(90, 48, 220, 23);
            var browse = new Button { Text = "Browse...", Bounds = new Rectangle(318, 47, 82, 25) };
            browse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Mod config (*.skudef)|*.skudef", Title = "Pick a mod's .skudef file" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        int i = modPaths.IndexOf(d.FileName);
                        if (i < 0) { modBox.Items.Add(Path.GetFileNameWithoutExtension(d.FileName)); modPaths.Add(d.FileName); i = modPaths.Count - 1; }
                        modBox.SelectedIndex = i;
                    }
            };
            Controls.AddRange(new Control[] { modBox, browse });

            Controls.Add(new Label { Text = "Version:", AutoSize = true, Location = new Point(20, 88) });
            verBox.Items.Add("Auto");
            foreach (int v in SkuVersions(game, lang)) verBox.Items.Add("1." + v);
            string lastVer = ReadIni(ini, "menu_ver");
            verBox.SelectedItem = lastVer == null || lastVer == "-1" ? "Auto" : "1." + lastVer;
            if (verBox.SelectedIndex < 0 && verBox.Items.Count > 0) verBox.SelectedIndex = 0;
            verBox.Bounds = new Rectangle(90, 84, 90, 23);
            Controls.Add(verBox);

            Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, 122, 420, 2) });
            var play = new Button { Text = "Play", Bounds = new Rectangle(244, 134, 75, 23) };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(325, 134, 75, 23) };
            play.Click += (s, e) =>
            {
                SetIni(ini, "menu_mod", ModConfig ?? "");
                SetIni(ini, "menu_ver", Version.ToString());
                DialogResult = DialogResult.OK;
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };
            Controls.AddRange(new Control[] { play, cancel });
            AcceptButton = play;
            CancelButton = cancel;
        }
    }

    static string GameName(string game)
    {
        string leaf = Path.GetFileName(Path.GetFullPath(game).TrimEnd('\\'));
        var g = Games.FirstOrDefault(x => string.Equals(x[0], leaf, StringComparison.OrdinalIgnoreCase));
        return g != null ? g[1] : leaf;
    }

    static List<int> SkuVersions(string game, string lang)
    {
        string newest = LatestSkuDef(game, lang);
        string prefix = Regex.Replace(Path.GetFileName(newest), @"_1\.\d+\.SkuDef$", "", RegexOptions.IgnoreCase);
        return Directory.GetFiles(game, prefix + "_1.*.SkuDef")
            .Select(f => Regex.Match(Path.GetFileName(f), @"_1\.(\d+)\.SkuDef$", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value))
            .OrderByDescending(v => v).ToList();
    }

    // Documents\<game>\Mods\<mod>\*.skudef, same place the stock launcher looks
    static List<Tuple<string, string>> FindMods(string game)
    {
        var mods = new List<Tuple<string, string>>();
        foreach (string leaf in UserDataFolders(game))
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), leaf, "Mods");
            if (!Directory.Exists(dir)) continue;
            foreach (string mod in Directory.GetDirectories(dir))
            {
                string sku = Directory.GetFiles(mod, "*.skudef").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (sku != null) mods.Add(Tuple.Create(Path.GetFileName(mod), sku));
            }
        }
        return mods;
    }

    static List<string> UserDataFolders(string game)
    {
        var names = new List<string>();
        string target = Path.GetFullPath(game).TrimEnd('\\');
        try
        {
            using (var ea = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Electronic Arts\Electronic Arts"))
                if (ea != null)
                    foreach (string name in ea.GetSubKeyNames())
                        using (var k = ea.OpenSubKey(name))
                        {
                            string dir = (k.GetValue("Install Dir") ?? k.GetValue("installpath") ?? k.GetValue("InstallPath")) as string;
                            string leaf = k.GetValue("UserDataLeafName") as string;
                            if (dir != null && leaf != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
                                names.Add(leaf);
                        }
        }
        catch { }
        string gname = GameName(game);
        if (gname == "Red Alert 3") names.Add("Red Alert 3");
        if (gname == "Red Alert 3 Uprising") names.Add("Red Alert 3 Uprising");
        if (gname == "Tiberium Wars") names.Add("Command & Conquer 3 Tiberium Wars");
        if (gname == "Kane's Wrath") names.Add("Command & Conquer 3 Kane's Wrath");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void SetIni(string ini, string key, string value)
    {
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
        int i = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*" + key + @"\s*=", RegexOptions.IgnoreCase));
        if (i >= 0) lines[i] = key + "=" + value; else lines.Add(key + "=" + value);
        File.WriteAllLines(ini, lines);
    }

    static string IniPath(string game) { return Path.Combine(game, "RA3HighFps.ini"); }

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

    // ---- finding games ----

    // steam folder, display name
    static readonly string[][] Games =
    {
        new[] { "Command and Conquer Red Alert 3",          "Red Alert 3" },
        new[] { "Command and Conquer 3 Tiberium Wars",      "Tiberium Wars" },
        new[] { "Command and Conquer 3 - Kane's Wrath",     "Kane's Wrath" },
        new[] { "Command and Conquer Red Alert 3 Uprising",   "Red Alert 3 Uprising" },
        new[] { "Command and Conquer Red Alert 3 - Uprising", "Red Alert 3 Uprising" },
    };

    static List<Tuple<string, string>> FindGames()
    {
        var libs = new List<string>();
        string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                       ?? @"C:\Program Files (x86)\Steam";
        libs.Add(steam.Replace('/', '\\'));
        string vdf = Path.Combine(libs[0], @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        var found = new List<Tuple<string, string>>();
        foreach (var g in Games)
            foreach (string l in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string p = Path.Combine(l, @"steamapps\common", g[0]);
                if (IsGameFolder(p)) { found.Add(Tuple.Create(g[1], p)); break; }
            }

        // ea app / origin / disc
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts\Electronic Arts", @"SOFTWARE\WOW6432Node\Electronic Arts",
                                        @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\Electronic Arts\Electronic Arts" })
        {
            try
            {
                using (var ea = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (ea == null) continue;
                    foreach (string name in ea.GetSubKeyNames())
                        using (var k = ea.OpenSubKey(name))
                        {
                            string dir = (k.GetValue("Install Dir") ?? k.GetValue("installpath") ?? k.GetValue("InstallPath")) as string;
                            if (dir == null || !IsGameFolder(dir)) continue;
                            string full = Path.GetFullPath(dir).TrimEnd('\\');
                            if (found.Any(f => string.Equals(Path.GetFullPath(f.Item2).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))) continue;
                            string label = (k.GetValue("ProductName") ?? k.GetValue("displayname") ?? name) as string;
                            found.Add(Tuple.Create(label.Replace("Command & Conquer ", "") + " (EA app)", full));
                        }
                }
            }
            catch { }
        }
        return found;
    }

    static bool IsSteamInstall(string dir) { return dir.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0; }

    static string MakeShortcut(string exe, string name)
    {
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + " (FPS Unlocker).lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic s = shell.CreateShortcut(lnk);
        s.TargetPath = exe;
        s.Arguments = "--play";
        s.WorkingDirectory = Path.GetDirectoryName(exe);
        s.IconLocation = exe + ",0";
        s.Save();
        return lnk;
    }

    static bool IsGameFolder(string dir)
    {
        try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*_1.*.SkuDef").Length > 0; }
        catch { return false; }
    }

    // newest skudef for the game's language (non-english installs have english ones too)
    static string LatestSkuDef(string game, string lang, int runver = -1)
    {
        var skus = Directory.GetFiles(game, "*_1.*.SkuDef")
            .Select(f => new { f, m = Regex.Match(Path.GetFileName(f), @"^.+?_([A-Za-z]+)_1\.(\d+)\.SkuDef$") })
            .Where(x => x.m.Success)
            .Select(x => new { x.f, lang = x.m.Groups[1].Value.ToLowerInvariant(), ver = int.Parse(x.m.Groups[2].Value) })
            .ToList();
        if (skus.Count == 0) throw new Exception("No SkuDef found in " + game);

        // --lang, then registry, then windows, then english
        var wanted = new[] { lang, RegistryLanguage(game),
                             CultureInfo.CurrentUICulture.Parent.EnglishName.Split(' ')[0].ToLowerInvariant(), "english" };
        string pick = wanted.FirstOrDefault(l => l != null && skus.Any(s => s.lang == l)) ?? skus[0].lang;
        var mine = skus.Where(s => s.lang == pick).ToList();
        if (runver >= 0)
        {
            var exact = mine.FirstOrDefault(s => s.ver == runver);
            if (exact == null) throw new Exception("-runver 1." + runver + ": no " + pick + " SkuDef for that version in " + game);
            return exact.f;
        }
        return mine.OrderByDescending(s => s.ver).First().f;
    }

    // steam writes the language ("German", "English (US)") to the EA key for this folder
    static string RegistryLanguage(string game)
    {
        string target = Path.GetFullPath(game).TrimEnd('\\');
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts\Electronic Arts", @"SOFTWARE\Electronic Arts\Electronic Arts" })
        {
            try
            {
                using (var ea = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (ea == null) continue;
                    foreach (string name in ea.GetSubKeyNames())
                        using (var k = ea.OpenSubKey(name))
                        {
                            string dir = (k.GetValue("Install Dir") ?? k.GetValue("installpath") ?? k.GetValue("InstallPath")) as string;
                            if (dir == null || !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase)) continue;
                            string l = (k.GetValue("language") ?? k.GetValue("Language")) as string;
                            if (!string.IsNullOrEmpty(l)) return l.Split(' ')[0].ToLowerInvariant();
                        }
                }
            }
            catch { }
        }
        return null;
    }

    static string SetExe(string sku)
    {
        foreach (string line in File.ReadAllLines(sku))
            if (line.StartsWith("set-exe ", StringComparison.OrdinalIgnoreCase))
                return line.Substring(8).Trim();
        throw new Exception("SkuDef has no set-exe line.");
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
                bool cmp6 = false, push6 = false;
                for (int k = i + 12; k < i + 24; k++)
                {
                    if (img[k] == 0x83 && img[k + 1] >= 0xF8 && img[k + 2] == 0x06) cmp6 = true;   // cmp reg,6
                    if (img[k] == 0x6A && img[k + 1] == 0x06) push6 = true;                        // push 6 ...
                    if (push6 && img[k] == 0x3B) cmp6 = true;                                      // ... cmp reg,reg
                }
                if (cmp6) { sites.Add(va); tick++; }
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
            if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)code.Count, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, site, code.ToArray());
            VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)code.Count, old, out old);
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
        if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)p.Count, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, site, p.ToArray());
        VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)p.Count, old, out old);
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
            if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)13, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, site, p.ToArray());
            VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)13, old, out old);
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
        if (!VirtualProtectEx(proc, (IntPtr)slot, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, slot, u(stubVa));
        VirtualProtectEx(proc, (IntPtr)slot, (UIntPtr)4, old, out old);
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
        if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)10, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, site, p.ToArray());
        VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)10, old, out old);
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
            if (!VirtualProtectEx(proc, (IntPtr)s, (UIntPtr)1, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, s, new[] { frames });
            VirtualProtectEx(proc, (IntPtr)s, (UIntPtr)1, old, out old);
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
            if (!VirtualProtectEx(proc, (IntPtr)s.Item1, (UIntPtr)6, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, s.Item1, p.ToArray());
            VirtualProtectEx(proc, (IntPtr)s.Item1, (UIntPtr)6, old, out old);
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
                VirtualProtectEx(proc, (IntPtr)op, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out o);
                Write(proc, op, u(fpmFineVa));
                VirtualProtectEx(proc, (IntPtr)op, (UIntPtr)4, o, out o);
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
            if (!VirtualProtectEx(proc, (IntPtr)site.Item1, (UIntPtr)site.Item2.Length, PAGE_EXECUTE_READWRITE, out old))
                throw new Exception("VirtualProtectEx failed.");
            Write(proc, site.Item1, site.Item2);
            VirtualProtectEx(proc, (IntPtr)site.Item1, (UIntPtr)site.Item2.Length, old, out old);
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
        if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, site, call.ToArray());
        VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)5, old, out old);
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

// for the drop-in dll (ExecuteInDefaultAppDomain wants static int Method(string))
public static class DllEntry
{
    public static int Run(string dir) { return Program.InProcess(dir); }
}
