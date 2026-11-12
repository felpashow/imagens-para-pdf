using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FotosParaPdf
{
    internal static class Instance
    {
        private static readonly string Id = "FotosParaPdf.v11." + WindowsIdentity.GetCurrent().User.Value + "." + System.Diagnostics.Process.GetCurrentProcess().SessionId;
        private static readonly string PipeName = Id;
        private static readonly string MutexName = @"Local\" + Id;

        internal static void Run(string[] paths)
        {
            bool owner;
            using (var mutex = new Mutex(true, MutexName, out owner))
            {
                if (!owner) { Send(paths); return; }
                try
                {
                    using (var form = new MainForm(paths))
                    {
                        // Create the handle before accepting messages from secondary invocations.
                        IntPtr handle = form.Handle;
                        using (var receiver = new Receiver(delegate(string[] received)
                        {
                            if (!form.IsDisposed) form.BeginInvoke((Action)delegate { if (!form.IsDisposed) form.ReceiveFiles(received); });
                        }))
                        {
                            receiver.Start();
                            Application.Run(form);
                        }
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void Send(string[] paths)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                pipe.Connect(15000);
                using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                {
                    writer.Write(paths.Length);
                    foreach (string path in paths) writer.Write(Path.GetFullPath(path));
                    writer.Flush();
                    using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                        if (reader.ReadByte() != 1) throw new IOException("Não foi possível adicionar as imagens à janela aberta.");
                }
            }
        }

        private sealed class Receiver : IDisposable
        {
            private readonly Action<string[]> receive;
            private readonly object gate = new object();
            private NamedPipeServerStream current;
            private bool stopped;
            internal Receiver(Action<string[]> receive) { this.receive = receive; }
            internal void Start() { Task.Run((Action)Listen); }
            private void Listen()
            {
                while (true)
                {
                    NamedPipeServerStream pipe;
                    lock (gate)
                    {
                        if (stopped) return;
                        var security = new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                        current = pipe;
                    }
                    try
                    {
                        pipe.WaitForConnection();
                        using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                        {
                            int count = reader.ReadInt32();
                            if (count < 0 || count > 10000) throw new IOException("Seleção inválida.");
                            var paths = new string[count];
                            for (int i = 0; i < count; i++)
                            {
                                paths[i] = reader.ReadString();
                                if (paths[i].Length > 32767) throw new IOException("Caminho inválido.");
                            }
                            receive(paths);
                            pipe.WriteByte(1);
                            pipe.Flush();
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                    finally { pipe.Dispose(); lock (gate) { if (current == pipe) current = null; } }
                }
            }
            public void Dispose()
            {
                lock (gate) { stopped = true; if (current != null) current.Dispose(); }
            }
        }

        internal static void TestServer(string reportPath, int expected)
        {
            bool owner;
            using (var mutex = new Mutex(true, MutexName, out owner))
            {
                if (!owner) throw new InvalidOperationException("Já existe uma instância aberta para este usuário.");
                try
                {
                    var files = new List<string>();
                    using (var completed = new ManualResetEventSlim())
                    using (var receiver = new Receiver(delegate(string[] paths)
                    {
                        files.AddRange(paths);
                        if (Program.Prepare(files).Count >= expected)
                        {
                            File.WriteAllLines(reportPath, Program.Prepare(files), new UTF8Encoding(false));
                            completed.Set();
                        }
                    }))
                    {
                        receiver.Start();
                        if (!completed.Wait(30000)) throw new TimeoutException("As imagens não chegaram à instância principal.");
                        Thread.Sleep(100); // Let the last client receive its acknowledgement.
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
