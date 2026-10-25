using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace T7Drop
{
    /// <summary>
    /// One t7drop per user and data folder. A second start hands its work to the first through a
    /// named pipe that only this user may open: files from Send To ("send" and the paths), or
    /// "show" (the history window), each request ending with an empty line.
    /// </summary>
    static class Instance
    {
        /// <summary>Per user, session and data folder, so that a test copy (T7DROP_HOME) runs beside the real one.</summary>
        public static string Name
        {
            get
            {
                string home = Paths.Home.ToLowerInvariant();
                string hash;
                using (SHA256 sha = SHA256.Create())
                    hash = Hex.Of(sha.ComputeHash(Encoding.UTF8.GetBytes(home))).Substring(0, 12);
                return "T7Drop-" + Process.GetCurrentProcess().SessionId + "-" + hash;
            }
        }

        /// <summary>Asks the running instance to do something; false if none answered in time.</summary>
        public static bool Hand(string command, IEnumerable<string> lines, int timeoutMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
                    writer.WriteLine(command);
                    foreach (string line in lines)
                        writer.WriteLine(line);
                    writer.WriteLine();
                    writer.Flush();
                    var reader = new StreamReader(pipe, new UTF8Encoding(false));
                    return reader.ReadLine() == "ok";
                }
            }
            catch (Exception)
            {
                return false;  // TimeoutException, IOException
            }
        }
    }

    /// <summary>The running instance's end of the pipe; calls `handle` on its own thread.</summary>
    sealed class InstanceServer : IDisposable
    {
        readonly Action<string, List<string>> handle;
        readonly Thread thread;
        volatile bool stopping;

        public InstanceServer(Action<string, List<string>> handle)
        {
            this.handle = handle;
            thread = new Thread(Listen) { IsBackground = true, Name = "t7drop instance pipe" };
            thread.Start();
        }

        void Listen()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            // Not even this user from another computer: a pipe is open to the network otherwise.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            while (!stopping)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(Instance.Name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security))
                    {
                        pipe.WaitForConnection();
                        if (stopping)
                            return;
                        var reader = new StreamReader(pipe, new UTF8Encoding(false));
                        string command = reader.ReadLine();
                        var lines = new List<string>();
                        for (string line = reader.ReadLine(); !string.IsNullOrEmpty(line); line = reader.ReadLine())
                            lines.Add(line);
                        if (command != null)
                            handle(command, lines);
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
                        writer.WriteLine("ok");
                        writer.Flush();
                        pipe.WaitForPipeDrain();
                    }
                }
                catch (Exception e)
                {
                    if (stopping)
                        return;
                    Log.Write("instance pipe: " + e.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            Instance.Hand("stop", new string[0], 200);  // wakes WaitForConnection
        }
    }
}
