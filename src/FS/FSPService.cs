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
    /// <summary>
    /// WinFSP service that mounts and unmounts the <see cref="DavFS"/> file system as a Windows drive,
    /// and relays its events to the UI.
    /// </summary>
    public class FSPService : Service
    {
        private FileSystemHost Host;
        private DavFS davFs;
        /// <summary>
        /// Relays <see cref="DavFS"/> notifications about operations performed against the repository (used for the activity log).
        /// </summary>
        public event EventHandler<LogListItem> RepositoryActionPerformed;
        /// <summary>
        /// Relays the <see cref="DavFS"/> notification raised when the server rejects the credentials (HTTP 401).
        /// </summary>
        public event EventHandler RepositoryAuthenticationFailed;

        /// <summary>
        /// Creates the service, registered with WinFSP under the name "KS2DriveService".
        /// </summary>
        public FSPService() : base("KS2DriveService")
        {
        }

        /// <summary>
        /// Creates the <see cref="DavFS"/> file system from the configuration and mounts it on the configured drive letter.
        /// </summary>
        /// <param name="config">Application configuration (server, credentials, drive letter, cache and flush options...).</param>
        /// <exception cref="IOException">WinFSP failed to mount the file system.</exception>
        public void Mount(Configuration config)
        {
            davFs = new DavFS(config);
            davFs.RepositoryActionPerformed += (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
            davFs.RepositoryAuthenticationFailed += (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

            Host = new FileSystemHost(davFs);
            if (Host.Mount($"{config.DriveLetter}:", null, config.SyncOps, 0) < 0) throw new IOException("cannot mount file system");
        }

        /// <summary>
        /// Unmounts the file system and releases the WinFSP host.
        /// </summary>
        public void Unmount()
        {
            davFs.RepositoryActionPerformed -= (s, e) => { RepositoryActionPerformed?.Invoke(s, e); };
            davFs.RepositoryAuthenticationFailed -= (s, e) => { RepositoryAuthenticationFailed?.Invoke(s, e); };

            Host.Unmount();
            Host = null;
        }

        /// <summary>
        /// Called when the service stops: unmounts the file system if it is mounted.
        /// </summary>
        protected override void OnStop()
        {
            if (Host != null)
            {
                Unmount();
            }
        }
    }
}
