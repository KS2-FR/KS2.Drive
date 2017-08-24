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
        private FileSystemHost HostX;
        private FileSystemHost HostY;
        private davFS davFs;
        private Thread DebugTread;
        private const String PROGNAME = "memfs-dotnet";

        public FSPService() : base("KS2DriveService")
        {
        }

        protected override void OnStart(string[] Args)
        {
        }

        public void MountX(String DriveName, String URL, Int32 Mode, String Login, String Password)
        {
            HostX = new FileSystemHost(davFs = new davFS(true, 1024, 16 * 1024 * 1024, null, (WebDAVMode)Mode, URL, FlushMode.FlushAtCleanup, Login, Password));
            HostX.FileInfoTimeout = unchecked((UInt32)(-1));
            HostX.Prefix = null;
            HostX.FileSystemName = "DAVFS";

            //Start debug window
            DebugTread = new Thread(() => Application.Run(new DebugView(davFs)));
            DebugTread.Start();

            bool IsSync = false;
            if (HostX.Mount($"{DriveName}:", null, IsSync, 0) < 0) throw new IOException("cannot mount file system");
        }

        public void UnmountX()
        {
            DebugTread.Abort();
            HostX.Unmount();
            HostX = null;
        }

        public void MountY()
        {
            /*
            HostY = new FileSystemHost(davFs = new davFS(true, 1024, 16 * 1024 * 1024, null));
            HostY.FileInfoTimeout = unchecked((UInt32)(-1));
            HostY.Prefix = null;
            HostY.FileSystemName = "DAVFS";

            bool IsSync = false;
            if (HostY.Mount("Y:", null, IsSync, 0) < 0) throw new IOException("cannot mount file system");
            */
        }

        public void UnmountY()
        {
            HostY.Unmount();
            HostY = null;
        }

        protected override void OnStop()
        {
            if (HostX != null)
            {
                HostX.Unmount();
                HostX = null;
            }

            if (HostY != null)
            {
                HostY = null;
                HostY.Unmount();
            }
        }
    }
}
