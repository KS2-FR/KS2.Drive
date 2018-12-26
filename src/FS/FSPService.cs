using Fsp;
using KS2Drive.Config;
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
        public event EventHandler RepositoryAuthenticationFailed;

        public FSPService() : base("KS2DriveService")
        {
        }

        public void Mount(Configuration config)
        {
            davFs = new DavFS(config);
            davFs.RepositoryActionPerformed += (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
            davFs.RepositoryAuthenticationFailed += (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

            Host = new FileSystemHost(davFs);
            if (Host.Mount($"{config.DriveLetter}:", null, config.SyncOps, 0) < 0) throw new IOException("cannot mount file system");
        }

        public void Unmount()
        {
            davFs.RepositoryActionPerformed -= (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
            davFs.RepositoryAuthenticationFailed -= (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

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
