// C&C FPS Unlocker - TheeHorse 2026
// GPL v3 or later, see LICENSE. https://github.com/TheeHorse/CnC-FPS-Unlocker
//
// Replaces the old v1.6 launcher (RA3HighFps.exe). People still have "RA3HighFps.exe" %command% as their
// Steam launch option, and the old launcher patched the game before the new dll could, so the newer fixes
// never ran. This one just starts the game the way Steam asked and lets the dll do its thing.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

static class Forwarder
{
    [STAThread]
    static int Main(string[] args)
    {
        // the old options went before %command% (--fps 120 etc), the game's exe is the first .exe that exists
        int i = Array.FindIndex(args, a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (i < 0)
        {
            MessageBox.Show("This file is left over from an older version of the C&C FPS Unlocker and isn't needed anymore.\n\n" +
                "If it's in your Steam launch options you can clear them (right-click the game > Properties). " +
                "The game works either way.", "C&C FPS Unlocker", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
}
