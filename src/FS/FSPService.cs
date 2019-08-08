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
        private List<Tuple<Configuration, FileSystemHost, DavFS>> mounts;
        public event EventHandler<LogListItem> RepositoryActionPerformed;
        public event EventHandler RepositoryAuthenticationFailed;

        private ConfigurationUI configurationUI;

        public FSPService(ConfigurationUI configurationUI) : base("KS2DriveService")
        {
            mounts = new List<Tuple<Configuration, FileSystemHost, DavFS>>();
            this.configurationUI = configurationUI;
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

            mounts.Add(new Tuple<Configuration, FileSystemHost, DavFS>(config, Host, davFs));

            config.IsMounted = true;
            this.configurationUI.UpdateMountButton();
        }

        public void Unmount(Configuration config)
        {
            foreach(Tuple<Configuration, FileSystemHost, DavFS> mount in mounts)
            {
                if (config != mount.Item1) continue;
                mount.Item3.RepositoryActionPerformed -= (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
                mount.Item3.RepositoryAuthenticationFailed -= (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

                mount.Item2.Unmount();
                mount.Item2.Dispose();
            }

            // If we get here the drive has succesfully unmounted (or was not mounted in the first place).
            config.IsMounted = false;
            this.configurationUI.UpdateMountButton();
        }

        protected override void OnStop()
        {
            foreach (Tuple<Configuration, FileSystemHost, DavFS> mount in mounts)
            {
                mount.Item2.Dispose();
            }

            mounts = null;
        }
    }
}
