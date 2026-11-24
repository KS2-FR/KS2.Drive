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
        /// Unit tests only: when set, the mounted file system sends its requests to this handler (e.g. an in-memory server)
        /// instead of the network. Always null in the application.
        /// </summary>
        internal System.Net.Http.HttpMessageHandler TestHttpMessageHandler { get; set; }

        /// <summary>
        /// Creates the <see cref="DavFS"/> file system from the configuration and mounts it on the configured drive letter.
        /// </summary>
        /// <param name="config">Application configuration (server, credentials, drive letter, cache and flush options...).</param>
        /// <exception cref="IOException">WinFSP failed to mount the file system.</exception>
        public void Mount(Configuration config)
        {
            davFs = new DavFS(config, TestHttpMessageHandler);
            davFs.RepositoryActionPerformed += DavFs_RepositoryActionPerformed;
            davFs.RepositoryAuthenticationFailed += DavFs_RepositoryAuthenticationFailed;

            Host = new FileSystemHost(davFs);
            if (Host.Mount($"{config.DriveLetter}:", null, config.SyncOps, 0) < 0)
            {
                Host.Dispose();
                Host = null;
                DetachFileSystem();
                throw new IOException("cannot mount file system");
            }
        }

        /// <summary>
        /// Unmounts the file system and releases the WinFSP host. Does nothing if no file system is mounted.
        /// Events are detached only after the unmount, so that failures reported by the last cleanup/close operations still reach the UI.
        /// </summary>
        public void Unmount()
        {
            if (Host != null)
            {
                Host.Unmount();
                Host = null;
            }

            DetachFileSystem();
        }

        /// <summary>
        /// Detaches the event handlers from the current <see cref="DavFS"/> instance and releases it.
        /// </summary>
        private void DetachFileSystem()
        {
            if (davFs == null) return;
            davFs.RepositoryActionPerformed -= DavFs_RepositoryActionPerformed;
            davFs.RepositoryAuthenticationFailed -= DavFs_RepositoryAuthenticationFailed;
            davFs = null;
        }

        /// <summary>
        /// Relays <see cref="DavFS.RepositoryActionPerformed"/> to <see cref="RepositoryActionPerformed"/>.
        /// </summary>
        private void DavFs_RepositoryActionPerformed(object sender, LogListItem e)
        {
            RepositoryActionPerformed?.Invoke(sender, e);
        }

        /// <summary>
        /// Relays <see cref="DavFS.RepositoryAuthenticationFailed"/> to <see cref="RepositoryAuthenticationFailed"/>.
        /// </summary>
        private void DavFs_RepositoryAuthenticationFailed(object sender, EventArgs e)
        {
            RepositoryAuthenticationFailed?.Invoke(sender, e);
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
