using Fsp;
using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KS2Drive
{
    public class FSPService : Service
    {
        private List<Tuple<FileSystemHost, DavFS>> mounts;
        public event EventHandler<LogListItem> RepositoryActionPerformed;
        public event EventHandler RepositoryAuthenticationFailed;

        public FSPService() : base("KS2DriveService")
        {
            mounts = new List<Tuple<FileSystemHost, DavFS>>();
        }
        
        public void Mount(Configuration config)
        {
            if (!config.IsConfigured) throw new IOException("Configuration is not configured.");
            
            DavFS davFs = new DavFS(config);
            
            davFs.RepositoryActionPerformed += (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
            davFs.RepositoryAuthenticationFailed += (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };
            
            FileSystemHost Host = new FileSystemHost(davFs);
            davFs.Init(Host, config.Name, config.Name);
            if (Host.MountEx($"{config.DriveLetter}:", 64, null, true, 0) < 0) throw new IOException("cannot mount file system");

            mounts.Add(new Tuple<FileSystemHost, DavFS>(Host, davFs));
        }

        public void Unmount(Configuration config)
        {
            foreach(Tuple<FileSystemHost, DavFS> mount in mounts)
            {
                mount.Item2.RepositoryActionPerformed -= (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
                mount.Item2.RepositoryAuthenticationFailed -= (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

                mount.Item1.Unmount();
                mount.Item1.Dispose();
            }
        }

        protected override void OnStop()
        {
            foreach (Tuple<FileSystemHost, DavFS> mount in mounts)
            {
                mount.Item1.Dispose();
            }

            mounts = null;
        }
    }
}
