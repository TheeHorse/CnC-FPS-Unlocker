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
        // Particles simulate at their native 30 Hz, like the stock game.
        bool throttlePfx = true;
        // Experimental: also redirect the other fps reads (animation timing). ini: extra=all
        string extra = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "extra");
        // Experimental: also update cached "frames per logic tick" copies. ini: ticks=on
        bool ticks = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "ticks") == "on";
        bool menu = ReadIni(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RA3HighFps.ini"), "menu") == "on";   // mod / version picker on launch
        string check = null, game = null, lang = null;
        int runver = -1;   // -runver 1.12 (the stock launcher option): run that game version instead of the newest
        var pass = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--fps" && i + 1 < argv.Length) fps = int.Parse(argv[++i]);
            else if (argv[i] == "--check" && i + 1 < argv.Length) check = argv[++i];
            else if (argv[i] == "--pfx" && i + 1 < argv.Length) throttlePfx = argv[++i] != "off";
            else if (argv[i] == "--extra" && i + 1 < argv.Length) extra = argv[++i];
            else if (argv[i] == "--ticks" && i + 1 < argv.Length) ticks = argv[++i] == "on";
            else if (argv[i] == "--game" && i + 1 < argv.Length) game = argv[++i].Trim('"');
            else if (argv[i] == "--lang" && i + 1 < argv.Length) lang = argv[++i].ToLowerInvariant();   // e.g. --lang german
            // The stock launcher's own options: -runver picks the SkuDef version; -ui (its launcher window) has no
            // equivalent here, so it's dropped rather than passed to the game.
            else if (argv[i].Equals("-runver", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length) runver = int.Parse(argv[++i].Split('.').Last());
            else if (argv[i].Equals("-ui", StringComparison.OrdinalIgnoreCase)) continue;
            else if (argv[i] == "--play") continue;   // shortcut to the installed copy: just launch this folder's game
            else if (argv[i] == "--menu") menu = true;
            // apitrace's d3d9.dll wrapper (when present in the exe folder) writes its trace here.
            else if (argv[i] == "--trace" && i + 1 < argv.Length) Environment.SetEnvironmentVariable("TRACE_FILE", argv[++i]);
            // Steam runs us as `RA3HighFps.exe %command%`, so the first thing after our own
            // options is the game's launcher (RA3.exe, CNC3.exe, ...). Its folder is the game.
            else if (game == null && argv[i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(argv[i]))
                game = Path.GetDirectoryName(Path.GetFullPath(argv[i]));
            else pass.Add(argv[i].Contains(" ") ? "\"" + argv[i] + "\"" : argv[i]);
        }
        // Logic runs at 15 ticks/s, so each tick must span a whole number of frames.
        if (fps < 30 || fps > 240 || fps % 15 != 0)
            throw new Exception("fps must be a multiple of 15 between 30 and 240 (e.g. 60, 90, 120, 135, 165, 240).");

        if (check != null)
        {
            // Dry run against a given game executable: report what would be patched.
            byte[] c = File.ReadAllBytes(check);
            uint cr = FindRenderFps(c);
            var cs = FindPacingSites(c, cr, cr - 4);
            uint pm, ps = FindParticleSim(c, out pm);
            string report = string.Format("render_fps @0x{0:X} = {1}, logic_fps = {2}\nsites: {3}\nparticle sim call @0x{4:X} (target 0x{5:X})\n",
                cr, BitConverter.ToInt32(c, (int)(cr - ImageBase)), BitConverter.ToInt32(c, (int)(cr - 4 - ImageBase)),
                string.Join(", ", cs.Select(s => "0x" + s.ToString("X"))), ps, pm);
            report += string.Format("model transition step operand @0x{0:X}\n", FindModelTransitionStep(c));
            report += string.Format("structure build-up block @0x{0:X}\n", FindUnpackProgress(c));
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

        // Double-clicked copy that already lives in a game folder: that's the game.
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
        // A mod's skudef says which game version it's built for ("mod-game 1.12"); the stock
        // launcher switches to that version, so do the same unless -runver was given.
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
        // Logic advances one tick every fps/15 drawn frames, so a target the screen can't show
        // (vsync caps drawing at the refresh rate) runs the game in slow motion. Cap it at the
        // refresh rate, rounded down to a multiple of 15 (240 on a 120 Hz screen -> 120, 144 Hz -> 135).
        int hz = MonitorHz();
        if (hz >= 30 && fps > hz / 15 * 15) fps = Math.Max(30, hz / 15 * 15);
        string sku = LatestSkuDef(game, lang, runver);
        string exe = Path.Combine(game, SetExe(sku));

        byte[] img = File.ReadAllBytes(exe);
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
        SchedSite schedSite = FindScheduler(img);
        bool sched = schedSite != null;

        string cmd = "\"" + exe + "\" -config \"" + sku + "\"" + (pass.Count > 0 ? " " + string.Join(" ", pass) : "");
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
        PROCESS_INFORMATION pi;
        if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, game, ref si, out pi))
            throw new Exception("Could not start " + exe + " (error " + Marshal.GetLastWin32Error() + ").");

        try
        {
            // Layout: +0 render fps, +8 particle accumulator, +40h stub code.
            IntPtr mem = VirtualAllocEx(pi.hProcess, IntPtr.Zero, (UIntPtr)4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (mem == IntPtr.Zero) throw new Exception("VirtualAllocEx failed.");
            Write(pi.hProcess, (uint)mem, BitConverter.GetBytes(fps));
            if (sched)
            {
                // With the clock scheduler in charge at every frame rate, the phase dispatcher and the
                // tick-boundary check must always work one phase per call (their fps/15 >= 6 branch),
                // so those two sites read max(fps, 90) instead of the fps.
                List<uint> ratio = sites.Where(s => IsPhaseRatioSite(img, s)).ToList();
                Write(pi.hProcess, (uint)mem + 0x90, BitConverter.GetBytes(Math.Max(fps, 90)));
                Redirect(pi.hProcess, ratio, (uint)mem + 0x90);
                Redirect(pi.hProcess, sites.Except(ratio).ToList(), (uint)mem);
            }
            else Redirect(pi.hProcess, sites, (uint)mem);
            if (fades.Count > 0) PatchFadeFrameReads(pi.hProcess, img, fades, (uint)mem, (uint)mem + 0x440);   // drawable fade timers
            if (scrollSlot != 0 && fps > 30) PatchScrollBy(pi.hProcess, scrollSlot, scrollFunc, fps, (uint)mem + 0x4F0, (uint)mem + 0x4F8, (uint)mem + 0x3C0);   // camera scroll speed
            if (sched) PatchScheduler(pi.hProcess, img, schedSite, (uint)mem, (uint)mem);   // 15 ticks/s at any fps above 90
            if (limiter != 0) PatchLimiterRounding(pi.hProcess, limiter, limiterRA3, (uint)mem + 0x28, (uint)mem + 0x2C, (uint)mem + 0x280);   // exact frame pacing
            if (unpack != 0) PatchUnpack(pi.hProcess, img, unpack, (uint)mem + 0x200, sched ? (uint)mem + 0x3C : 0, (uint)mem + 0x4E0);   // Soviet/Empire build-up clock
            if (anim2d.Count > 0) PatchAnim2D(pi.hProcess, img, anim2d, (uint)mem, (uint)mem + 0x100);   // 30 Hz sprite-animation clock
            if (modelStep != 0)   // model transitions: 1/fps per drawn frame instead of 1/30
            {
                Write(pi.hProcess, (uint)mem + 0x10, BitConverter.GetBytes(1f / fps));
                Redirect(pi.hProcess, new List<uint> { modelStep }, (uint)mem + 0x10);
            }
            if (throttlePfx && pfxSite != 0)
                ThrottleParticles(pi.hProcess, pfxSite, pfxSim, (uint)mem, (uint)mem + 8, (uint)mem + 0x40);
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

    // Little window you get when you double-click the exe. Lists every supported game it
    // finds, installs into the ticked ones, then shows the Steam launch option for each.
    class SetupForm : Form
    {
        readonly ComboBox fpsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckedListBox gameList = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        readonly CheckBox menuBox = new CheckBox { Text = "Show a mod && version picker when the game starts", AutoSize = true };
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

            // Header strip
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
            for (int f = 30; f <= 240; f += 15) fpsBox.Items.Add(f + " fps");
            int current = games.Select(g => ReadIniFps(IniPath(g.Item2), 0)).FirstOrDefault(v => v > 0);
            int pick = current > 0 ? current : Math.Max(30, Math.Min(240, MonitorHz() / 15 * 15));
            fpsBox.SelectedItem = pick + " fps";
            if (fpsBox.SelectedIndex < 0) fpsBox.SelectedItem = "120 fps";
            fpsBox.Bounds = new Rectangle(100, y, 100, 23);
            Controls.Add(fpsBox);
            Controls.Add(new Label { AutoSize = true, Location = new Point(210, y + 4), ForeColor = SystemColors.GrayText,
                                     Text = "(your monitor: " + MonitorHz() + " Hz)" });

            menuBox.Location = new Point(20, y + 36);
            menuBox.Checked = games.Any(g => ReadIni(IniPath(g.Item2), "menu") == "on");
            Controls.Add(menuBox);
            y += 70;
            Controls.Add(Line(y));
            var install = new Button { Text = "Install", Bounds = new Rectangle(324, y + 13, 75, 23), Enabled = games.Count > 0 };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(405, y + 13, 75, 23) };
            cancel.Click += (s, e) => Close();   // this window isn't a dialog, so DialogResult alone does nothing
            install.Click += (s, e) => Install();
            Controls.AddRange(new Control[] { install, cancel });
            AcceptButton = install;
            CancelButton = cancel;
            ClientSize = new Size(500, y + 48);
            Shown += (s, e) => fpsBox.Focus();
        }

        // Etched separator like the ones in normal Windows setup programs.
        static Label Line(int y)
        {
            return new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, y, 500, 2) };
        }

        int SelectedFps() { return int.Parse(((string)fpsBox.SelectedItem).Split(' ')[0]); }

        // Copies this exe into each ticked game folder and saves the fps there.
        void Install()
        {
            var done = new List<Tuple<string, string>>();   // (game name, launch option)
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

    // Last page: one launch option per game, each with a Copy button.
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
                foreach (var tb in Controls.OfType<TextBox>()) tb.Select(0, 0);   // show the start of each line
                ok.Focus();
            };
        }
    }

    // Shown on launch when menu=on (or --menu): pick a mod and game version, like the old
    // -ui launcher window. Remembers the last choice in the ini.
    class MenuForm : Form
    {
        readonly ComboBox modBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox verBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly List<string> modPaths = new List<string> { null };   // index 0 = no mod
        public string ModConfig { get { return modPaths[modBox.SelectedIndex]; } }
        // -1 = Auto: the newest version, or the one the chosen mod asks for (mod-game 1.N)
        public int Version { get { string v = (string)verBox.SelectedItem; return v == "Auto" ? -1 : int.Parse(v.Split('.')[1]); } }

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

    // Display name for a game folder, from the Games table.
    static string GameName(string game)
    {
        string leaf = Path.GetFileName(Path.GetFullPath(game).TrimEnd('\\'));
        var g = Games.FirstOrDefault(x => string.Equals(x[0], leaf, StringComparison.OrdinalIgnoreCase));
        return g != null ? g[1] : leaf;
    }

    // Game versions (1.N) that have a SkuDef in the chosen language, newest first.
    static List<int> SkuVersions(string game, string lang)
    {
        string newest = LatestSkuDef(game, lang);
        string prefix = Regex.Replace(Path.GetFileName(newest), @"_1\.\d+\.SkuDef$", "", RegexOptions.IgnoreCase);
        return Directory.GetFiles(game, prefix + "_1.*.SkuDef")
            .Select(f => Regex.Match(Path.GetFileName(f), @"_1\.(\d+)\.SkuDef$", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value))
            .OrderByDescending(v => v).ToList();
    }

    // Mods in Documents\<game's user data folder>\Mods\<Mod>\*.skudef (newest skudef per mod),
    // which is where the stock launcher looks.
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

    // The game's folder name under Documents: the registry's UserDataLeafName, else the usual names.
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

    // Set key=value in the ini, keeping the other lines.
    static void SetIni(string ini, string key, string value)
    {
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
        int i = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*" + key + @"\s*=", RegexOptions.IgnoreCase));
        if (i >= 0) lines[i] = key + "=" + value; else lines.Add(key + "=" + value);
        File.WriteAllLines(ini, lines);
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
        string v = ReadIni(ini, "fps");
        int n;
        return v != null && int.TryParse(v, out n) ? n : fallback;
    }

    static string ReadIni(string ini, string key)
    {
        if (!File.Exists(ini)) return null;
        foreach (string line in File.ReadAllLines(ini))
        {
            var m = Regex.Match(line, @"^\s*" + key + @"\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase);   // whole value: paths have spaces
            if (m.Success && m.Groups[1].Value.Length > 0) return m.Groups[1].Value;
        }
        return null;
    }

    // ---- locating the game -------------------------------------------------

    // Steam folder name -> display name. All are SAGE games with a launcher that runs the
    // real exe from the newest <prefix>_<lang>_1.N.SkuDef.
    static readonly string[][] Games =
    {
        new[] { "Command and Conquer Red Alert 3",          "Red Alert 3" },
        new[] { "Command and Conquer 3 Tiberium Wars",      "Tiberium Wars" },
        new[] { "Command and Conquer 3 - Kane's Wrath",     "Kane's Wrath" },
        new[] { "Command and Conquer Red Alert 3 Uprising",   "Red Alert 3 Uprising" },   // Steam folder name
        new[] { "Command and Conquer Red Alert 3 - Uprising", "Red Alert 3 Uprising" },
    };

    // Installed supported games as (display name, folder).
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

        // EA app / Origin / retail installs register their folder under EA's registry keys.
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

    // Desktop shortcut that runs the unlocker in a (non-Steam) game folder with --play.
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

    // The launcher runs the highest-versioned <prefix>_<lang>_1.N.SkuDef for the game's
    // language (or 1.<runver> when -runver is given, e.g. 1.12 for mods that need it).
    // Non-English installs ship English SkuDefs too, so the language matters.
    static string LatestSkuDef(string game, string lang, int runver = -1)
    {
        var skus = Directory.GetFiles(game, "*_1.*.SkuDef")
            .Select(f => new { f, m = Regex.Match(Path.GetFileName(f), @"^.+?_([A-Za-z]+)_1\.(\d+)\.SkuDef$") })
            .Where(x => x.m.Success)
            .Select(x => new { x.f, lang = x.m.Groups[1].Value.ToLowerInvariant(), ver = int.Parse(x.m.Groups[2].Value) })
            .ToList();
        if (skus.Count == 0) throw new Exception("No SkuDef found in " + game);

        // Preference: --lang, the language Steam wrote to the registry, Windows' language, English.
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

    // Steam's install script writes the chosen language (e.g. "German", "English (US)") under the
    // game's Electronic Arts registry key. Find the key whose install path is this folder.
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

    // ---- signatures ----------------------------------------------------------
    // -1 is a wildcard; R/L are the 4-byte render_fps / logic_fps addresses.

    // mov eax,1000 / xor edx,edx / div [render_fps] / mov [frame_ms],eax
    static readonly int[] FrameMsSig = { 0xB8, 0xE8, 0x03, 0x00, 0x00, 0x33, 0xD2, 0xF7, 0x35, -1, -1, -1, -1, 0xA3 };

    static uint FindRenderFps(byte[] img)
    {
        var hits = Scan(img, FrameMsSig);
        if (hits.Count != 1) throw new Exception("Unsupported game build (frame limiter not found).");
        return BitConverter.ToUInt32(img, hits[0] + 9);
    }

    // Frame-pacing reads of render_fps, found by what the code does so the same rules
    // work for RA3 and C&C3. Never includes the fps*0.001 "framesPerMs" initializer:
    // the particle code uses that as a fixed 30ths-of-a-second clock.
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

            // Frame limiter: mov eax,1000 / xor edx,edx / div [fps]
            if (img[i - 9] == 0xB8 && img[i - 8] == 0xE8 && img[i - 7] == 0x03 && img[i - 2] == 0xF7 && img[i - 1] == 0x35)
            { sites.Add(va); continue; }

            // Frames per logic tick: mov eax,[fps] / xor edx,edx / div [logic], then compared with 6
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

            // Static initializers: push ecx / mov eax,[fps] / fild [fps] / (unsigned fixup) / op
            // Redirect 1000/fps (fdivr) and fps (fstp); keep anything multiplied (time units).
            if (img[i - 2] == 0x51 && img[i - 1] == 0xA1 && img[i + 4] == 0xDB && img[i + 5] == 0x05 && at(i + 6, R)
                && img[i + 10] == 0x85 && img[i + 12] == 0x7D && img[i + 14] == 0xD8 && img[i + 15] == 0x05)
            {
                byte op1 = img[i + 20], op2 = img[i + 21];
                if ((op1 == 0xD8 && op2 == 0x3D) || (op1 == 0xD9 && op2 == 0x1D)) sites.Add(va + 6);
                continue;
            }

            // Object field set to the frame rate in a constructor: mov eax,[fps] / mov [esi+x],eax
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

    // Every other plain read of render_fps (mostly `fild [fps]` float conversions in visual code),
    // minus anything that must stay 30: logic-rate derivations (fps/2, fps/logic), the particle
    // clock's "now" (`call getTime / imul eax,[fps]`) and time-unit initializers (fild then fmul).
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
            if (pacing.Contains(va) || pacing.Contains(va - 6) || pacing.Contains(va + 6)) continue;   // incl. the sign-test read next to a redirected fild
            string p2 = img[i - 2].ToString("X2") + img[i - 1].ToString("X2");
            string p3 = img[i - 3].ToString("X2") + p2;
            if (!(img[i - 1] == 0xA1 || twoByte.Contains(p2) || twoByte.Contains(p3))) continue;
            if (img[i + 4] == 0xD1 && img[i + 5] >= 0xE8 && img[i + 5] <= 0xEF) continue;          // fps/2 = logic rate
            bool divL = false;
            for (int k = i + 4; k < i + 10; k++)
                if (img[k] == 0xF7 && (img[k + 1] == 0x35 || img[k + 1] == 0x3D) && at(k + 2, L)) divL = true;
            if (divL) continue;                                                                    // fps / logic
            if (p3.StartsWith("0FAF") && img[i - 8] == 0xE8) continue;                             // call getTime / imul [fps]
            // Static initializer `push ecx / mov eax,[fps] / fild [fps] / ... / fmul`: a time unit.
            int s = img[i - 1] == 0xA1 ? i : (p2 == "DB05" ? i - 6 : -1);
            if (s > 0 && img[s - 2] == 0x51 && img[s - 1] == 0xA1 && img[s + 4] == 0xDB && img[s + 14] == 0xD8
                && img[s + 20] == 0xD8 && img[s + 21] == 0x0D) continue;
            list.Add(va);
        }
        return list;
    }

    // `mov eax,[fps] / xor edx,edx / div [logic]` whose result is stored into an object
    // (`mov [reg+x],eax` shortly after): cached "frames per logic tick" copies. If these keep
    // the stock value (2) while the main loop runs 8 frames per tick, anything paced by the
    // cached copy (e.g. construction progress) runs fps/30 times too fast.
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

    // "all", "none", or comma-separated indices/ranges into the list, e.g. "0-20,25".
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

    // Scripted model transitions (e.g. Soviet/Empire building unfold / build-up) advance a
    // 0..1 timer by the shared 1/30 s constant once per drawn frame:
    //     call [GameClient]->getFrame / movss xmm0,[obj+x] / addss xmm0,[1/30f]
    // At 120 fps that plays them 4x too fast. Returns the VA of the addss operand (only that
    // read moves to 1/fps; the shared constant has other users, e.g. pathfinding), or 0.
    // Credit: identified in CNCStuff/cnc3_fps_patch ("scripted-model transition step").
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

    // Anim2D (2D sprite animations, e.g. the construction progress sprites) picks the next
    // frame when `getFrame() - [anim+8] >= [anim+18h]`, i.e. it counts drawn frames and its
    // intervals are authored for 30 fps. All reads of getFrame() that feed [anim+8] must use
    // the same clock, so every `mov reg,[reg+74h] / call reg` followed by `mov [esi+8],eax`
    // or `sub eax,[esi+8] / cmp eax,[esi+18h]` is switched to a 30 Hz frame number.
    // Credit: identified in CNCStuff/cnc3_fps_patch (Anim2D frame reads).
    static List<uint> FindAnim2DFrameReads(byte[] img)
    {
        int end = TextEnd(img);
        var list = new List<uint>();
        for (int i = 0x1000; i < end - 16; i++)
        {
            // 8B 4x 74 = mov r32,[r32+74h] ; [83 C4 xx = add esp,imm8] ; FF Dx = call r32 (same register)
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

    // Stub: eax = ceil(GameClient->getFrame() * 30 / fps). ecx is the GameClient, as at the call site.
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
            // `mov r,[r+74h] / call r` (5 bytes) -> `call stub`;
            // `mov r,[r+74h] / add esp,xx / call r` (8 bytes) -> `add esp,xx / call stub`.
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

    // The frame limiter waits trunc(66.67 ms / framesPerTick) per frame. Truncating to whole
    // milliseconds is harmless at 30 fps (33 ms) but at 120 fps gives 8 ms = 125 fps, so the game
    // runs about 4% fast (same at 60 and 240; 90 is close to exact). The patch keeps the dropped
    // fraction and carries it into the next frame (8, 8, 9, ... ms), so the average is exact.
    // RA3 truncates inline with fistp; C&C3 calls the CRT's _ftol (credit: CNCStuff/cnc3_fps_patch
    // found the C&C3 block).
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

    // Returns the patch site; ra3Style = inline fistp (8 bytes), otherwise a 5-byte call to _ftol.
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
        var s = new List<byte> { 0xD8, 0x05 }; s.AddRange(u(accVa));               // fadd [acc]   (leftover from last frame)
        if (ra3Style)
        {
            // rounding mode is already "truncate" here (the game set it for its own fistp)
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
        s.AddRange(new byte[] { 0xD9, 0x1D }); s.AddRange(u(accVa));               // fstp [acc]   (new leftover, 0..1 ms)
        if (ra3Style) { s.AddRange(new byte[] { 0x8B, 0x35 }); s.AddRange(u(tmpVa)); }   // mov esi,[tmp]
        else { s.Add(0xA1); s.AddRange(u(tmpVa)); }                                        // mov eax,[tmp]
        s.Add(0xC3);
        Write(proc, stubVa, s.ToArray());

        var p = new List<byte> { 0xE8 }; p.AddRange(u(stubVa - (site + 5)));
        if (ra3Style) p.AddRange(new byte[] { 0x90, 0x90, 0x90 });   // replaces fistp qword [esp+14h] / mov esi,[esp+14h]
        uint old;
        if (!VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)p.Count, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, site, p.ToArray());
        VirtualProtectEx(proc, (IntPtr)site, (UIntPtr)p.Count, old, out old);
    }

    // Drawable fade in/out (dying units, stealth, some effect objects) gets its duration in 30 fps
    // frames (ms * framesPerMs) but stamps and measures time with GameClient::getFrame(), the real
    // drawn-frame count, so at 120 fps fades finished 4x early. All five reads of that clock (the
    // setters that stamp [obj+338h] and the per-frame update) are switched to a 30 Hz frame number.
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
        return list.Count == 5 ? list : new List<uint>();   // exactly the known set, or leave it alone
    }

    // Each site is `mov ecx,[TheGameClient] / mov r,[ecx] / mov r,[r+74h] / call r` (13 bytes).
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

    // Camera scrolling (edge, arrow keys, right-drag) adds a per-frame step, so at 120 fps it
    // scrolled 4x as fast. Every input path ends in the tactical view's scrollBy(Coord2D*), so its
    // vtable slot is pointed at a wrapper that scales the delta by 30/fps. (Same approach as
    // CNCStuff/cnc3_fps_patch uses for C&C3.)
    const string ScrollByPattern = "A1 ?? ?? ?? ?? 83 EC 60 80 B8 BC 00 00 00 00 56 8B F1 74 06 80 7E 48 00 75 09 80 BE 35 27 00 00 00 74 09 33 C0 5E 83 C4 60 C2 04 00";

    const string ScrollByPatternCnc3 = "55 8B EC A1 ?? ?? ?? ?? 83 EC 64 80 B8 CC 00 00 00 00 53 8B D9 74 0D 80 7B 44 00 74 07 33 C0 E9";

    // Returns the vtable slot holding scrollBy (0 if not found or ambiguous).
    static uint FindScrollBySlot(byte[] img, out uint func)
    {
        func = FindUnique(img, ScrollByPattern);
        if (func == 0) func = FindUnique(img, ScrollByPatternCnc3);   // Tiberium Wars / Kane's Wrath
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
        s.AddRange(new byte[] { 0xC7, 0x44, 0x24, 0x04 }); s.AddRange(u(tmpVa));      // mov [esp+4],tmp  (scaled copy)
        s.Add(0xE9); s.AddRange(u(func - (stubVa + (uint)s.Count + 4)));              // jmp scrollBy
        Write(proc, stubVa, s.ToArray());
        uint old;
        if (!VirtualProtectEx(proc, (IntPtr)slot, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out old))
            throw new Exception("VirtualProtectEx failed.");
        Write(proc, slot, u(stubVa));
        VirtualProtectEx(proc, (IntPtr)slot, (UIntPtr)4, old, out old);
    }

    // Tiny assembler for the stubs: raw bytes, labels, short jumps and rel32 calls/jumps.
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
        // Jump to a label, given the short opcode (EB = jmp, 7x = jcc); always emitted in the rel32 form.
        public void J(byte op, string name)
        {
            if (op == 0xEB) B.Add(0xE9); else { B.Add(0x0F); B.Add((byte)(0x80 + (op - 0x70))); }
            fixes.Add(Tuple.Create(B.Count, name)); D(0);
        }
        public void Rel(byte op, uint target) { B.Add(op); D(target - (org + (uint)B.Count + 4)); }            // call/jmp rel32
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

    // All three games split each 15 Hz logic tick into 6 phases. At 90 fps and up the engine runs
    // one phase per drawn frame and starts the next tick right after phase 6, so a tick lasts
    // 6 frames: RA3 at 120 fps ran 20 ticks/s, and a PC that couldn't hold its target (or C&C3,
    // which fell below it) ran in slow motion; below 90 the stock batching was also a bit off.
    // The per-frame engine update is hooked at every frame rate and ticks are scheduled by the
    // clock instead: a tick starts every 66.67 ms (kept in 1/3 ms
    // units so it's exact), the phases that are due by the clock run each frame (several at once
    // on a slow PC, none on idle frames), and the interpolation value drawables use to blend
    // between ticks is set to the time fraction, so movement stays smooth at any frame rate.
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

    // A pacing site that computes fps / logic and then compares it with 6 (the phase dispatcher and
    // the tick-boundary check): `mov r,[fps]` operand at `site`, then `xor edx,edx / div [logic]`,
    // then `cmp reg,6` or `push 6` within a few bytes.
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
        if (a != 0 && b != 0 && b - a == 0x124 && lim != 0)   // RA3
        {
            var s = new SchedSite { Advance = a + 0x11, Exit = b + 0xD, Phase = 0x58, Interp = 0x60, Dispatch = 0x90, TimeFn = CallTarget(img, lim - 0x27) };
            return s.TimeFn != 0 ? s : null;
        }
        a = FindUnique(img, SchedPatternCnc3A); b = FindUnique(img, SchedPatternCnc3B); lim = FindUnique(img, LimiterPatternCnc3);
        if (a != 0 && b != 0 && (b + 9) - (a + 9) == 0xCF && lim != 0)   // Tiberium Wars / Kane's Wrath
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
        uint stubA = mem + 0x800, stubB = mem + 0xA00;
        uint exitGlobal = BitConverter.ToUInt32(img, (int)(site.Exit + 2 - ImageBase));   // mov ecx,[global] at the exit
        byte ph = site.Phase;
        Write(proc, k200, BitConverter.GetBytes(200f));

        // Stub A, called in place of `mov ecx,[esi+phase] / cmp ecx,6` (esi = engine).
        var a = new Asm(stubA);
        a.E(0x89, 0x35); a.D(eng);                                   // mov [engine],esi  (for the build-up clock)
        a.E(0x50, 0x52, 0x51);                                       // push eax / push edx / push ecx
        a.E(0xA1); a.D(fpsVa); a.E(0x33, 0xD2, 0xB9, 0x0F, 0, 0, 0, 0xF7, 0xF1); a.E(0xA3); a.D(r);   // r = fps / 15
        a.Rel(0xE8, site.TimeFn);                                   // eax = ms (timeGetTime)
        a.E(0x8D, 0x04, 0x40); a.E(0xA3); a.D(now3);                 // now3 = ms * 3
        a.E(0x83, 0x3D); a.D(t0); a.E(0x00); a.J(0x75, "have");     // first time: tick starts now
        a.E(0xA3); a.D(t0);
        a.L("have");
        a.E(0x8B, 0x4E, ph);                                         // ecx = phase
        a.E(0x83, 0x3D); a.D(pend); a.E(0x00); a.J(0x74, "nopend");  // did we start a tick last frame?
        a.E(0xC7, 0x05); a.D(pend); a.D(0);
        a.E(0x83, 0xF9, 0x06); a.J(0x72, "nopend");                 // phase wrapped: it really started
        a.E(0x81, 0x2D); a.D(t0); a.D(200);                          // held back (network): undo, retry
        a.L("nopend");
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // edx = t = now3 - tickStart
        a.E(0x83, 0xF9, 0x06); a.J(0x72, "mid");
        // phase 6 done: start the next tick once 66.67 ms have passed
        a.E(0x81, 0xFA); a.D(200); a.J(0x7C, "idle");
        a.E(0x81, 0x05); a.D(t0); a.D(200);                          // tickStart += 200
        a.E(0xC7, 0x05); a.D(pend); a.D(1);
        a.E(0x8B, 0xD0, 0x2B, 0x15); a.D(t0);                        // more than 2 ticks behind: resync
        a.E(0x81, 0xFA); a.D(400); a.J(0x7E, "pass");
        a.E(0xA3); a.D(t0); a.J(0xEB, "pass");
        a.L("mid");
        // phases due = 1 + ceil(t * 6 / 200), capped at 6
        a.E(0x8D, 0x04, 0x52, 0x85, 0xC0); a.J(0x7D, "pos"); a.E(0x33, 0xC0);
        a.L("pos");
        a.E(0x83, 0xC0, 0x63, 0x33, 0xD2, 0x51, 0xB9, 0x64, 0, 0, 0, 0xF7, 0xF1, 0x59, 0x40);
        a.E(0x83, 0xF8, 0x06); a.J(0x76, "cap"); a.E(0xB8, 6, 0, 0, 0);
        a.L("cap");
        a.E(0x3B, 0xC1); a.J(0x76, "idle");                          // nothing due this frame
        a.L("loop");                                                 // run all but the last due phase here
        a.E(0x8D, 0x51, 0x01, 0x3B, 0xD0); a.J(0x73, "pass");
        a.E(0x89, 0x56, ph, 0x50, 0x52, 0x8B, 0xCE, 0x8B, 0x16, 0xFF, 0x92); a.D(site.Dispatch);   // [phase]=n / dispatch(n)
        a.E(0x58, 0x8B, 0x4E, ph); a.J(0xEB, "loop");
        a.L("idle");                                                 // skip the phase code this frame
        a.E(0x59, 0x5A, 0x58, 0x83, 0xC4, 0x04, 0x57); a.Rel(0xE9, site.Exit);
        a.L("pass");                                                 // original: advance one phase / start a tick
        a.E(0x59, 0x5A, 0x58, 0x8B, 0x4E, ph, 0x83, 0xF9, 0x06, 0xC3);
        Write(proc, stubA, a.Done(0x200));

        // Stub B, called in place of `mov ecx,[global]` at the exit: interpolation = time fraction of the tick.
        var b = new Asm(stubB);
        b.E(0x50);
        b.E(0xA1); b.D(now3); b.E(0x2B, 0x05); b.D(t0);
        b.E(0x85, 0xC0); b.J(0x7D, "b1"); b.E(0x33, 0xC0);
        b.L("b1");
        b.E(0x3D); b.D(200); b.J(0x7E, "b2"); b.E(0xB8); b.D(200);
        b.L("b2");
        b.E(0x50, 0xDB, 0x04, 0x24, 0xD8, 0x35); b.D(k200); b.E(0xD9, 0x5E, site.Interp, 0x58);   // [interp] = t / 200
        b.L("done");
        b.E(0x58, 0x8B, 0x0D); b.D(exitGlobal); b.E(0xC3);
        Write(proc, stubB, b.Done(0x100));

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

    // Soviet/Empire build-up (StructureUnpackUpdate progress, used for both the rising model and the
    // on-building bar) converts the build's start tick and duration to 30 fps client frames
    // (tick / 15 * 1000 * framesPerMs), then measures "now" with GameClient::getFrame(), the real
    // drawn-frame count. At 120 fps "now" runs 4x ahead of the start stamp, so the build looks done
    // at once. Both getFrame reads are switched to the current logic tick put through the same
    // conversion, which is what the drawn-frame count equals at stock 30 fps.
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
            if (found >= 0) return 0;   // not unique: leave it alone
            found = i;
        }
        return found < 0 ? 0 : ImageBase + (uint)found;
    }

    // With engineVar (a slot the scheduler hook fills with the engine pointer every frame) the
    // build-up runs in 1/240 s units and "now" includes the engine's between-ticks fraction, so the
    // rise and the bar move every frame instead of in 15 steps a second.
    static void PatchUnpack(IntPtr proc, byte[] img, uint block, uint stubVa, uint engineVar, uint fpmFineVa)
    {
        Func<uint, uint> rd = va => BitConverter.ToUInt32(img, (int)(va - ImageBase));
        Func<uint, byte[]> u = BitConverter.GetBytes;
        uint logic = rd(block + 2), conv = block + 0x17 + 5 + rd(block + 0x18), k1000 = rd(block + 0x1E), fpm = rd(block + 0x2F);
        bool fine = engineVar != 0 && img[conv + 0x16 - ImageBase] == 0xD8 && img[conv + 0x17 - ImageBase] == 0x0D
                    && rd(block + 0x6D) == fpm && rd(block + 0xAD) == fpm;
        if (fine)
        {
            uint spf = rd(conv + 0x18);   // seconds per logic frame, as used by the conversion
            Write(proc, fpmFineVa, BitConverter.GetBytes(0.24f));   // 8x framesPerMs: 1/240 s units
            var f = new List<byte> { 0x51, 0x8B, 0x0D }; f.AddRange(u(logic));         // push ecx / mov ecx,[TheGameLogic]
            f.AddRange(new byte[] { 0xDB, 0x41, 0x50, 0xA1 }); f.AddRange(u(engineVar)); // fild [ecx+50h] / mov eax,[engine]
            f.AddRange(new byte[] { 0x85, 0xC0, 0x74, 0x03, 0xD8, 0x40, 0x60 });        // test eax,eax / jz +3 / fadd [eax+60h] (tick fraction)
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(spf));                  // fmul [secondsPerFrame]
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(k1000));                // fmul [1000.0]
            f.AddRange(new byte[] { 0xD8, 0x0D }); f.AddRange(u(fpmFineVa));            // fmul [0.24]
            f.AddRange(new byte[] { 0x83, 0xEC, 0x08, 0xD9, 0x3C, 0x24, 0x0F, 0xB7, 0x04, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00,
                                    0x89, 0x44, 0x24, 0x04, 0xD9, 0x6C, 0x24, 0x04, 0xDB, 0x5C, 0x24, 0x04, 0xD9, 0x2C, 0x24,
                                    0x8B, 0x44, 0x24, 0x04, 0x83, 0xC4, 0x08, 0x59, 0xC3 });   // truncate / pop ecx / ret
            Write(proc, stubVa, f.ToArray());
            // The start stamp and duration conversions use the same fine unit.
            foreach (uint op in new[] { block + 0x2F, block + 0x6D, block + 0xAD })
            {
                uint o;
                VirtualProtectEx(proc, (IntPtr)op, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out o);
                Write(proc, op, u(fpmFineVa));
                VirtualProtectEx(proc, (IntPtr)op, (UIntPtr)4, o, out o);
            }
        }
        var s = new List<byte> { 0x51, 0x8B, 0x0D }; s.AddRange(u(logic));   // fallback: whole logic ticks in 30 fps units
        s.AddRange(new byte[] { 0xFF, 0x71, 0x50 });                           // push [ecx+50h]  (current logic frame)
        s.Add(0xE8); s.AddRange(u(conv - (stubVa + (uint)s.Count + 4)));        // call frames->seconds (ret 4)
        s.AddRange(new byte[] { 0xD8, 0x0D }); s.AddRange(u(k1000));            // fmul [1000.0]
        s.AddRange(new byte[] { 0xD8, 0x0D }); s.AddRange(u(fpm));              // fmul [framesPerMs]
        s.AddRange(new byte[] { 0xD8, 0x89, 0xBC, 0x01, 0x00, 0x00 });          // fmul [ecx+1BCh]  (game speed, as the original)
        s.AddRange(new byte[] { 0x83, 0xEC, 0x08, 0xD9, 0x3C, 0x24, 0x0F, 0xB7, 0x04, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00,
                                0x89, 0x44, 0x24, 0x04, 0xD9, 0x6C, 0x24, 0x04, 0xDB, 0x5C, 0x24, 0x04, 0xD9, 0x2C, 0x24,
                                0x8B, 0x44, 0x24, 0x04, 0x83, 0xC4, 0x08 });   // truncate to int (same rounding mode as the original)
        s.AddRange(new byte[] { 0x59, 0xC3 });                                  // pop ecx / ret
        if (!fine) Write(proc, stubVa, s.ToArray());

        // site 1: mov edx,[eax+74h] / fldcw [esp+12h] / call edx  ->  fldcw [esp+12h] / call stub
        uint s1 = block + 0xD0;
        var p1 = new List<byte> { 0xD9, 0x6C, 0x24, 0x12, 0xE8 }; p1.AddRange(u(stubVa - (s1 + 9)));
        // site 2: mov ecx,[TheGameClient] / mov edx,[ecx] / mov eax,[edx+74h] / call eax  ->  call stub / nops
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

    // End of the .text section in the file (raw offset == RVA in these games).
    static int TextEnd(byte[] img)
    {
        int pe = BitConverter.ToInt32(img, 0x3C);
        int sec = pe + 24 + BitConverter.ToUInt16(img, pe + 20);
        return (int)(BitConverter.ToUInt32(img, sec + 20) + BitConverter.ToUInt32(img, sec + 16));
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
