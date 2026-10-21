using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace T7Drop
{
    /// <summary>"Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "T7Drop";

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        /// <summary>True when Windows starts this exe; false when it starts none, or a copy elsewhere.</summary>
        public static bool IsEnabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    return string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(ValueName, Command);
                else
                    key.DeleteValue(ValueName, false);
            }
        }
    }
}
