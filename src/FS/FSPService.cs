using Fsp;
using KS2Drive.Debug;
using KS2Drive.FS;
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KS2Drive
{
    public class FSPService : Service
    {
        private FileSystemHost Host;
        private DavFS davFs;

#if DEBUG
        private Thread DebugTread;
        private DebugView DebugWindow;
#endif

        public FSPService() : base("KS2DriveService")
        {
        }

        public void Mount(String DriveName, String URL, Int32 Mode, String Login, String Password, KernelCacheMode KernelMode, bool SyncOps)
        {
            davFs = new DavFS((WebDAVMode)Mode, URL, FlushMode.FlushAtCleanup, KernelMode, Login, Password);
            Host = new FileSystemHost(davFs);

            if (Host.Mount($"{DriveName}:", null, SyncOps, 0) < 0) throw new IOException("cannot mount file system");

#if DEBUG
            //Start debug window
            DebugWindow = new DebugView(davFs);
            DebugTread = new Thread(() => Application.Run(DebugWindow));
            DebugTread.Start();
#endif
        }

        public void Unmount()
        {
#if DEBUG
            if (DebugTread != null && (DebugTread.ThreadState & ThreadState.Running) == ThreadState.Running)
            {
                if (DebugWindow.InvokeRequired) DebugWindow.Invoke(new MethodInvoker(() => DebugWindow.Close()));
                else DebugWindow.Close();
            }
#endif
            Host.Unmount();
            Host = null;
        }

        protected override void OnStop()
        {
            if (Host != null)
            {
                Unmount();
            }
        }
    }
}
