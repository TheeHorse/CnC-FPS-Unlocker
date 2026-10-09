// SAGE Unlocked - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/SAGE-Unlocked
//
// Replaces the old v1.6 launcher (RA3HighFps.exe). People still have "RA3HighFps.exe" %command% as their
// Steam launch option, and the old launcher patched the game before the new dll could, so the newer fixes
// never ran. This one just starts the game the way Steam asked and lets the dll do its thing.
//
// Linux (Proton/Wine): the dll can't start .NET there, but Proton runs this exe fine. So when this exe sits
// in the game folder in place of the game's launcher (RA3.exe, CNC3.exe, CNC3EP1.exe), it starts the game
// itself and has CnCFpsUnlocker.dll patch it from outside, like the v1.6 launcher did.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Forwarder
{
    [STAThread]
    static int Main(string[] args)
    {
        // the old options went before %command% (--fps 120 etc), the game's exe is the first .exe that exists
        int i = Array.FindIndex(args, a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (i < 0)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            if (IsWine() && IsGameFolder(here))
            {
                try { return LaunchPatched(here, args); }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "SAGE Unlocked", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            MessageBox.Show("This file is left over from an older version of SAGE Unlocked (formerly C&C FPS Unlocker) and isn't needed anymore.\n\n" +
                "If it's in your Steam launch options you can clear them (right-click the game > Properties). " +
                "The game works either way.", "SAGE Unlocked", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        var start = new ProcessStartInfo(args[i], string.Join(" ", args.Skip(i + 1).Select(Quote)))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[i]))
        };
        // wait so Steam sees the game as running until it closes
        using (var p = Process.Start(start))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    // windows command line quoting, so arguments arrive exactly as Steam passed them
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        return sb.Append('"').ToString();
    }

    // ---- linux ----

    static bool IsWine()
    {
        IntPtr ntdll = GetModuleHandle("ntdll.dll");
        return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
    }

    static bool IsGameFolder(string dir)
    {
        try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*_1.*.SkuDef").Length > 0; }
        catch { return false; }
    }

    // does what the stock launcher does (pick the SkuDef, run its set-exe with -config), but suspended so the
    // fixes go in before the game's own startup code runs
    static int LaunchPatched(string game, string[] args)
    {
        int runver = -1;   // -runver 1.12, the stock launcher's option
        var pass = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("-runver", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) runver = int.Parse(args[++i].Split('.').Last());
            else if (args[i].Equals("-ui", StringComparison.OrdinalIgnoreCase)) continue;   // the launcher's own window, nothing to pass on
            else pass.Add(args[i]);
        }
        // a mod's skudef says which game version it needs ("mod-game 1.12"), the stock launcher switches to it
        int mi = pass.FindIndex(a => a.Equals("-modConfig", StringComparison.OrdinalIgnoreCase));
        if (runver < 0 && mi >= 0 && mi + 1 < pass.Count && File.Exists(pass[mi + 1]))
            foreach (string line in File.ReadAllLines(pass[mi + 1]))
            {
                var mg = Regex.Match(line, @"^\s*mod-game\s+1\.(\d+)", RegexOptions.IgnoreCase);
                if (mg.Success) { runver = int.Parse(mg.Groups[1].Value); break; }
            }

        string sku = LatestSkuDef(game, runver);
        string exe = Path.Combine(game, SetExe(sku));
        string dir = Path.GetDirectoryName(exe);
        string dll = Path.Combine(dir, "CnCFpsUnlocker.dll");
        if (!File.Exists(dll))
            throw new Exception("CnCFpsUnlocker.dll isn't in " + dir + ".\n\nCopy it there from the zip (see the install table in the README).");

        string cmd = Quote(exe) + " -config " + Quote(sku) + (pass.Count > 0 ? " " + string.Join(" ", pass.Select(Quote)) : "");
        // the game inherits this. tells the drop-in dll (if it's there too) that we've patched already, so it doesn't
        // try .net (fails under proton) and overwrite our log with that error
        Environment.SetEnvironmentVariable("CNCFPS_LAUNCHER", "1");
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
        PROCESS_INFORMATION pi;
        if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, game, ref si, out pi))
            throw new Exception("Couldn't start " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
        string note;
        try
        {
            // the dll writes SAGEUnlocked.log. if it fails the game still runs, just without the fixes
            var entry = Assembly.LoadFrom(dll).GetType("DllEntry");
            int r = (int)entry.GetMethod("Launch").Invoke(null, new object[] { pi.hProcess, exe, dir });
            note = r == 1 ? "patched" : r == 2 ? "already patched" : "patching failed (see above)";
        }
        catch (Exception e) { note = "couldn't run CnCFpsUnlocker.dll: " + (e.InnerException ?? e); }
        // what got started, so a log from a modded install says what happened
        try { File.AppendAllText(Path.Combine(dir, "SAGEUnlocked.log"), "RA3HighFps.exe started: " + cmd + "\r\n" + note + "\r\n"); } catch { }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        // wait so Steam sees the game as running until it closes
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        CloseHandle(pi.hProcess);
        return (int)code;
    }

    // highest <prefix>_<lang>_1.N.SkuDef for the game's language (or 1.<runver>)
    static string LatestSkuDef(string game, int runver)
    {
        var skus = Directory.GetFiles(game, "*_1.*.SkuDef")
            .Select(f => new { f, m = Regex.Match(Path.GetFileName(f), @"^.+?_([A-Za-z]+)_1\.(\d+)\.SkuDef$") })
            .Where(x => x.m.Success)
            .Select(x => new { x.f, lang = x.m.Groups[1].Value.ToLowerInvariant(), ver = int.Parse(x.m.Groups[2].Value) })
            .ToList();
        if (skus.Count == 0) throw new Exception("No SkuDef found in " + game);
        var wanted = new[] { RegistryLanguage(game), CultureInfo.CurrentUICulture.Parent.EnglishName.Split(' ')[0].ToLowerInvariant(), "english" };
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

    // steam writes the chosen language ("German", "English (US)") under the game's EA registry key
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
                            string d = (k.GetValue("Install Dir") ?? k.GetValue("installpath") ?? k.GetValue("InstallPath")) as string;
                            if (d == null || !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase)) continue;
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

    const uint CREATE_SUSPENDED = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
}
