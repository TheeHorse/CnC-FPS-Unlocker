// C&C FPS Unlocker setup - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/CnC-FPS-Unlocker
//
// just a file copier: finds the games and puts the drop-in files (d3d9.dll or dinput8.dll,
// CnCFpsUnlocker.dll, RA3HighFps.ini) next to each game's real exe. the files come from the
// folders next to this setup (the release zip). the game loads the dll itself, nothing else runs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    [STAThread]
    static void Main()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.Run(new SetupForm());
    }

    class SetupForm : Form
    {
        readonly ComboBox fpsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckedListBox gameList = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        float zoomValue = 1f;
        List<Tuple<string, string>> games;   // name, folder

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
            using (var s = typeof(Setup).Assembly.GetManifestResourceStream("banner.jpg"))
                if (s != null) banner.Image = Image.FromStream(s);
            Controls.Add(banner);
            Controls.Add(Line(90));

            games = FindGames();
            Controls.Add(new Label { Text = "Install C&C FPS Unlocker", AutoSize = true, Location = new Point(20, 106),
                                     Font = new Font(Font, FontStyle.Bold), UseMnemonic = false });
            Controls.Add(new Label { AutoSize = true, Location = new Point(20, 132), UseMnemonic = false,
                                     Text = games.Count > 0 ? "Install for these games:" : "No games found automatically. Use \"Add game folder...\" below." });

            var tips = new ToolTip();
            gameList.Bounds = new Rectangle(20, 152, 460, Math.Max(3, games.Count) * 20 + 6);
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

            foreach (var g in games)
            {
                float z;
                if (float.TryParse(ReadIni(IniPath(g.Item2), "zoom"), NumberStyles.Float, CultureInfo.InvariantCulture, out z) && z > 1f) { zoomValue = z; break; }
            }
            var extras = new Button { Text = "Extras...", Bounds = new Rectangle(20, y + 36, 90, 23) };
            extras.Click += (s, e) => { using (var x = new ExtrasForm(zoomValue)) if (x.ShowDialog(this) == DialogResult.OK) zoomValue = x.Zoom; };
            var addFolder = new Button { Text = "Add game folder...", Bounds = new Rectangle(118, y + 36, 130, 23) };
            Controls.AddRange(new Control[] { extras, addFolder });
            y += 74;
            Controls.Add(Line(y));
            var install = new Button { Text = "Install", Bounds = new Rectangle(324, y + 13, 75, 23), Enabled = games.Count > 0 };
            // for games it didn't find (EA app / Origin / disc in odd places)
            addFolder.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Pick the game's install folder (the one with the .SkuDef files, e.g. ...\\Red Alert 3)" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    string dir = d.SelectedPath.TrimEnd('\\');
                    if (!IsGameFolder(dir)) { MessageBox.Show(this, "That doesn't look like a supported game folder (no .SkuDef files in it).", "Setup"); return; }
                    if (games.Any(g => string.Equals(Path.GetFullPath(g.Item2).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))) return;
                    games.Add(Tuple.Create(GameName(dir), dir));
                    gameList.Items.Add(games.Last().Item1, true);
                    install.Enabled = true;
                }
            };
            var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(405, y + 13, 75, 23) };
            cancel.Click += (s, e) => Close();   // not a dialog
            install.Click += (s, e) => Install();
            Controls.AddRange(new Control[] { install, cancel });
            AcceptButton = install;
            CancelButton = cancel;
            ClientSize = new Size(500, y + 48);
            Shown += (s, e) => fpsBox.Focus();
        }

        static Label Line(int y) { return new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(0, y, 500, 2) }; }

        int SelectedFps() { return int.Parse(((string)fpsBox.SelectedItem).Split(' ')[0]); }

        void Install()
        {
            var done = new List<string>();
            bool oldLauncher = false;
            try
            {
                foreach (int i in gameList.CheckedIndices)
                {
                    string dir = games[i].Item2;
                    InstallDropIn(dir, SelectedFps(), zoomValue);
                    oldLauncher |= File.Exists(Path.Combine(dir, "RA3HighFps.exe"));
                    done.Add(games[i].Item1);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show(this, "Couldn't write to the game folder:\n\n" + ex.Message + "\n\nRight-click the setup and pick \"Run as administrator\".", "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't install:\n\n" + ex.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (done.Count == 0) { MessageBox.Show(this, "Tick at least one game.", "Setup"); return; }
            string msg = "Installed at " + SelectedFps() + " fps for:\n\n" + string.Join("\n", done) +
                         "\n\nJust start the games like you normally do. Run this setup again to change the fps.";
            if (oldLauncher)
                msg += "\n\nUsed an older version? Clear the old Launch Options in Steam (right-click the game > Properties). " +
                       "It still works if you leave it, but you don't need it anymore.";
            MessageBox.Show(this, msg, "Setup complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }

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
            foreach (float a in Amounts) amountBox.Items.Add(a.ToString("0.##", CultureInfo.InvariantCulture) + "x");
            int idx = Array.FindIndex(Amounts, a => Math.Abs(a - current) < 0.01f);
            amountBox.SelectedIndex = idx >= 0 ? idx : 1;   // 1.5x
            amountBox.Bounds = new Rectangle(95, 65, 70, 23);
            amountBox.Enabled = zoomBox.Checked;
            zoomBox.CheckedChanged += (s, e) => amountBox.Enabled = zoomBox.Checked;
            Controls.Add(amountBox);
            Controls.Add(new Label { AutoSize = false, Bounds = new Rectangle(34, 96, 330, 80), ForeColor = SystemColors.GrayText,
                                     Text = "Red Alert 3 only. Works in skirmish and campaign; online and LAN games always use the normal zoom.\n\n" +
                                            "1.75x can cut off the top of the screen on maps with big height differences (water, cliffs)." });

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(208, 186, 75, 23) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(289, 186, 75, 23) };
            Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(380, 222);
        }
    }

    // ---- installing ----

    // folder of the game's real exe (from the newest skudef's set-exe line), that's where the dll goes
    static string ExeFolder(string game) { return Path.GetDirectoryName(Path.Combine(game, SetExe(LatestSkuDef(game)))); }

    static string IniPath(string game)
    {
        try { return Path.Combine(ExeFolder(game), "RA3HighFps.ini"); }
        catch { return Path.Combine(game, "RA3HighFps.ini"); }
    }

    static void InstallDropIn(string game, int fps, float zoom)
    {
        string exe = Path.Combine(game, SetExe(LatestSkuDef(game)));
        string dir = Path.GetDirectoryName(exe);
        string proxy = exe.EndsWith(".game", StringComparison.OrdinalIgnoreCase) ? "d3d9.dll" : "dinput8.dll";   // ra3 : tw/kw
        string here = AppDomain.CurrentDomain.BaseDirectory;
        string src = new[] { here }.Concat(Directory.GetDirectories(here))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, proxy)) && File.Exists(Path.Combine(d, "CnCFpsUnlocker.dll")));
        if (src == null)
            throw new Exception("Couldn't find " + proxy + " and CnCFpsUnlocker.dll next to the setup. Unzip the whole release zip and run the setup from inside that folder.");
        foreach (string name in new[] { proxy, "CnCFpsUnlocker.dll" })
            File.Copy(Path.Combine(src, name), Path.Combine(dir, name), true);
        string ini = Path.Combine(dir, "RA3HighFps.ini");
        if (!File.Exists(ini)) File.WriteAllLines(ini, new[] { "; C&C FPS Unlocker settings (fps: multiple of 15, 30-240; zoom: ra3 only, 1 = off)" });
        SetIni(ini, "fps", fps.ToString());
        SetIni(ini, "zoom", zoom.ToString("0.##", CultureInfo.InvariantCulture));
    }

    static void SetIni(string ini, string key, string value)
    {
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
        int i = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*" + key + @"\s*=", RegexOptions.IgnoreCase));
        if (i >= 0) lines[i] = key + "=" + value; else lines.Add(key + "=" + value);
        File.WriteAllLines(ini, lines);
    }

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
        new[] { "Command and Conquer Red Alert 3",            "Red Alert 3" },
        new[] { "Command and Conquer 3 Tiberium Wars",        "Tiberium Wars" },
        new[] { "Command and Conquer 3 - Kane's Wrath",       "Kane's Wrath" },
        new[] { "Command and Conquer Red Alert 3 Uprising",   "Red Alert 3 Uprising" },
        new[] { "Command and Conquer Red Alert 3 - Uprising", "Red Alert 3 Uprising" },
    };

    static string GameName(string game)
    {
        string leaf = Path.GetFileName(Path.GetFullPath(game).TrimEnd('\\'));
        var g = Games.FirstOrDefault(x => string.Equals(x[0], leaf, StringComparison.OrdinalIgnoreCase));
        return g != null ? g[1] : leaf;
    }

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

    static bool IsGameFolder(string dir)
    {
        try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*_1.*.SkuDef").Length > 0; }
        catch { return false; }
    }

    // newest skudef for the game's language (non-english installs have english ones too)
    static string LatestSkuDef(string game)
    {
        var skus = Directory.GetFiles(game, "*_1.*.SkuDef")
            .Select(f => new { f, m = Regex.Match(Path.GetFileName(f), @"^.+?_([A-Za-z]+)_1\.(\d+)\.SkuDef$") })
            .Where(x => x.m.Success)
            .Select(x => new { x.f, lang = x.m.Groups[1].Value.ToLowerInvariant(), ver = int.Parse(x.m.Groups[2].Value) })
            .ToList();
        if (skus.Count == 0) throw new Exception("No SkuDef found in " + game);
        var wanted = new[] { RegistryLanguage(game), CultureInfo.CurrentUICulture.Parent.EnglishName.Split(' ')[0].ToLowerInvariant(), "english" };
        string pick = wanted.FirstOrDefault(l => l != null && skus.Any(s => s.lang == l)) ?? skus[0].lang;
        return skus.Where(s => s.lang == pick).OrderByDescending(s => s.ver).First().f;
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
}
