using Fsp;
using KS2Drive.FS;
using KS2Drive.Log;
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
        public event EventHandler<LogListItem> RepositoryActionPerformed;

        public FSPService() : base("KS2DriveService")
        {
        }

        public void Mount(String DriveName, String URL, Int32 Mode, String Login, String Password, FlushMode FlushMode, KernelCacheMode KernelMode, bool SyncOps, bool PreLoadFolders)
        {
            davFs = new DavFS((WebDAVMode)Mode, URL, FlushMode, KernelMode, Login, Password, PreLoadFolders);
            davFs.RepositoryActionPerformed += (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };

            Host = new FileSystemHost(davFs);
            if (Host.Mount($"{DriveName}:", null, SyncOps, 0) < 0) throw new IOException("cannot mount file system");
        }

        public void Unmount()
        {
            davFs.RepositoryActionPerformed -= (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };

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
