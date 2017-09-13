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
        private davFS davFs;
        private Thread DebugTread;
        private const String PROGNAME = "memfs-dotnet";

        public FSPService() : base("KS2DriveService")
        {
        }

        protected override void OnStart(string[] Args)
        {
        }

        public void Mount(String DriveName, String URL, Int32 Mode, String Login, String Password)
        {
            Host = new FileSystemHost(davFs = new davFS((WebDAVMode)Mode, URL, FlushMode.FlushAtWrite, Login, Password));
            Host.FileInfoTimeout = unchecked((UInt32)(-1));
            Host.Prefix = null;
            Host.FileSystemName = "davFS";
            
            //TEMP : mount as network drive
            Host.Prefix = @"\test\go";
            //TEMP

            //Start debug window
            DebugTread = new Thread(() => Application.Run(new DebugView(davFs)));
            DebugTread.Start();

            bool IsSync = false;
            //TEMP
            //IsSync = true;
            //TEMP
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
