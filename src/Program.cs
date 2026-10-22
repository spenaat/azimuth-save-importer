using System;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Azimuth Save Importer")]
[assembly: AssemblyProduct("Azimuth Save Importer")]
[assembly: AssemblyDescription("Imports Forza Horizon 2 Xbox saves (downloaded with XBsync) into Azimuth.")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace AzimuthSaveImporter
{
    static class Program
    {
        public const string Version = "1.0.0";

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                // The exe is a Windows app; borrow the console it was started from for CLI output.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    try { AttachConsole(-1); Console.WriteLine(); } catch { }
                }
                return Cli.Run(args);
            }
#if NO_GUI
            return Cli.Run(new[] { "--help" });
#else
            // Sharp text on high-DPI screens; WinForms then scales the layout (AutoScaleMode.Dpi).
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try { SetProcessDPIAware(); } catch { }
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new Gui.MainForm());
            return 0;
#endif
        }
    }
}
