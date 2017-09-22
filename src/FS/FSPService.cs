using Fsp;
using KS2Drive.Debug;
using KS2Drive.FS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        public void Mount(String DriveName, String URL, Int32 Mode, String Login, String Password)
        {
            try
            {
                davFs = new DavFS((WebDAVMode)Mode, URL, FlushMode.FlushAtCleanup, Login, Password);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            Host = new FileSystemHost(davFs);

            bool IsSync = false;
#if DEBUG
            IsSync = true;
#endif

            if (Host.Mount($"{DriveName}:", null, IsSync, 0) < 0) throw new IOException("cannot mount file system");

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
