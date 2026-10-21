using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

// The version comes from versionName in android/app/build.gradle.kts (one version for both apps):
// scripts/build.ps1 writes it into obj/Version.cs. Updater compares it with the latest release tag.
[assembly: AssemblyTitle("t7drop")]
[assembly: AssemblyDescription("Files, pictures, texts and links between this computer and your phone, end-to-end encrypted")]
[assembly: AssemblyProduct("t7drop")]
// Without it .NET runs the exe as if built for 4.0, whose HTTPS lacks TLS 1.2.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace T7Drop
{
    static class Program
    {
        /// <summary>
        /// T7Drop.exe starts the tray app; T7Drop.exe --send &lt;files&gt; (Explorer's Send To) sends
        /// files, through the running instance if there is one. A second plain start shows the
        /// running instance's history window. T7Drop.exe --exit ends the running instance.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool updated = Updater.FinishUpdate(args, Application.ExecutablePath);
            string[] files = args.Length > 0 && args[0] == SendTo.Argument ? args.Skip(1).ToArray() : new string[0];
            bool exit = args.Length > 0 && args[0] == "--exit";

            bool firstInstance;
            using (new Mutex(true, @"Local\" + Instance.Name, out firstInstance))
            {
                if (exit)
                    return firstInstance || Instance.Hand("exit", new string[0], 5000) ? 0 : 1;
                if (!firstInstance)
                {
                    if (Instance.Hand(files.Length > 0 ? "send" : "show", files, 15000))
                        return 0;
                    MessageBox.Show(files.Length > 0 ? "t7drop is busy starting; try again in a moment."
                        : "t7drop is already running. Its icon may be hidden behind the ^ arrow in the notification area.",
                        "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                Application.ThreadException += (sender, e) =>
                {
                    Log.Write("error: " + e.Exception);
                    MessageBox.Show(e.Exception.ToString(), "t7drop error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                // So that "await" continues on this thread. WinForms would install it only with the first control.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                TrayApp app;
                try
                {
                    app = new TrayApp(updated, files);
                }
                catch (Exception e)
                {
                    Log.Write("could not start: " + e);
                    MessageBox.Show("t7drop could not start:\n" + e.Message, "t7drop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                Application.Run(app);
                return 0;
            }
        }
    }
}
