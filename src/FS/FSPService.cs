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
        private Thread DebugTread;

        public FSPService() : base("KS2DriveService")
        {
        }

        public void Mount(String DriveName, String URL, Int32 Mode, String Login, String Password)
        {
            DavFS davFs;
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
            Host.FileInfoTimeout = unchecked((UInt32)(-1));
            Host.Prefix = null;
            Host.FileSystemName = "davFS";
            Host.Prefix = $@"\{new Uri(URL).Authority}"; //mount as network drive

#if DEBUG
            //Start debug window
            DebugTread = new Thread(() => Application.Run(new DebugView(davFs)));
            DebugTread.Start();
#endif
            bool IsSync = false;
            if (Host.Mount($"{DriveName}:", null, IsSync, 0) < 0) throw new IOException("cannot mount file system");
        }

        public void Unmount()
        {
            if (DebugTread != null) DebugTread.Abort();
            Host.Unmount();
            Host = null;
        }

        protected override void OnStop()
        {
            if (Host != null)
            {
                Host.Unmount();
                Host = null;
            }
        }
    }
}
