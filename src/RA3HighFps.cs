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
        string check = null, game = null, lang = null;
        var pass = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--fps" && i + 1 < argv.Length) fps = int.Parse(argv[++i]);
            else if (argv[i] == "--check" && i + 1 < argv.Length) check = argv[++i];
            else if (argv[i] == "--pfx" && i + 1 < argv.Length) throttlePfx = argv[++i] != "off";
            else if (argv[i] == "--extra" && i + 1 < argv.Length) extra = argv[++i];
            else if (argv[i] == "--ticks" && i + 1 < argv.Length) ticks = argv[++i] == "on";
            else if (argv[i] == "--game" && i + 1 < argv.Length) game = argv[++i];
            else if (argv[i] == "--lang" && i + 1 < argv.Length) lang = argv[++i].ToLowerInvariant();   // e.g. --lang german
            else if (argv[i] == "--play") continue;   // shortcut to the installed copy: just launch this folder's game
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
        string sku = LatestSkuDef(game, lang);
        string exe = Path.Combine(game, SetExe(sku));

        byte[] img = File.ReadAllBytes(exe);
        uint render = FindRenderFps(img);
        List<uint> sites = FindPacingSites(img, render, render - 4);
        sites.AddRange(SelectSites(FindExtraSites(img, render, render - 4, sites), extra));
        if (ticks) sites.AddRange(FindTickStores(img, render, render - 4));
        uint pfxSim, pfxSite = FindParticleSim(img, out pfxSim);

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
            Redirect(pi.hProcess, sites, (uint)mem);
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
                                     Text = games.Count > 0 ? "Install for these games:" : "No supported games found (Steam versions of RA3, C&C3, Kane's Wrath)." });

            var tips = new ToolTip();
            int listH = Math.Max(1, games.Count) * 20 + 6;
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

            y += 40;
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
                    File.WriteAllLines(IniPath(dir), new[] { "; C&C FPS Unlocker settings (multiple of 15, 30-240)", "fps=" + SelectedFps() });
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
            var m = Regex.Match(line, @"^\s*" + key + @"\s*=\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
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
        return found;
    }

    static bool IsGameFolder(string dir)
    {
        try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*_1.*.SkuDef").Length > 0; }
        catch { return false; }
    }

    // The launcher runs the highest-versioned <prefix>_<lang>_1.N.SkuDef for the game's
    // language. Non-English installs ship English SkuDefs too, so the language matters.
    static string LatestSkuDef(string game, string lang)
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
        return skus.Where(s => s.lang == pick).OrderByDescending(s => s.ver).First().f;
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
