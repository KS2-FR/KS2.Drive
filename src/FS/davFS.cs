using Fsp;
using KS2Drive.Config;
using KS2Drive.Log;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using KS2Drive.WebDav;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace KS2Drive.FS
{
    /// <summary>
    /// WinFSP user-mode file system that exposes a remote WebDAV repository as a Windows drive.
    /// Each WinFSP callback is translated into WebDAV calls through <see cref="WebDavClient"/>; file metadata
    /// (<see cref="FileNode"/>) is kept in a <see cref="CacheManager"/>, and file contents are held in memory
    /// (<c>FileNode.FileData</c>) while the file has open handles, and for a short time after. Modified contents are uploaded
    /// by <see cref="Flush"/>, <see cref="Cleanup"/> or <see cref="Close"/> (<see cref="FlushMode.FlushAtCleanup"/>, the only mode).
    /// </summary>
    public class DavFS : FileSystemBase
    {
        public event EventHandler<LogListItem> RepositoryActionPerformed;

        /// <summary>
        /// Raised at most once per mount, the first time the server rejects the credentials (HTTP 401).
        /// Handlers must not unmount the drive synchronously: the event is raised from inside a WinFSP callback.
        /// </summary>
        public event EventHandler RepositoryAuthenticationFailed;
        private static Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Set to 1 once <see cref="RepositoryAuthenticationFailed"/> has been raised, so that parallel operations
        /// failing with a 401 do not notify the application several times.
        /// </summary>
        private Int32 AuthenticationFailureNotified = 0;

        private UInt32 MaxFileNodes;
        private UInt32 MaxFileSize;
        private String VolumeLabel;
        private String DAVServer;
        private string DAVServeurAuthority;
        private KernelCacheMode kernelCacheMode;
        private bool MountAsNetworkDrive;
        private String DAVLogin;
        private String DAVPassword;
        private WebDAVMode WebDAVMode;
        private String DocumentLibraryPath;

        private CacheManager Cache;

        private const UInt16 MEMFS_SECTOR_SIZE = 4096;
        private const UInt16 MEMFS_SECTORS_PER_ALLOCATION_UNIT = 1;

        /// <summary>
        /// Maximum number of simultaneous HTTP connections to the WebDAV server (.NET Framework allows only 2 by default: Office
        /// saving a document, Explorer reading thumbnails and the background folder refreshes then queue behind each other).
        /// </summary>
        internal const Int32 MaxConnectionsPerServer = 8;

        /// <summary>
        /// HttpClient of this mount, shared by all the requests (connections are reused). Disposed by <see cref="Unmounted"/>.
        /// </summary>
        private readonly HttpClient Http;

        /// <summary>
        /// WebDAV client of this mount.
        /// </summary>
        internal readonly WebDavClient Client;

        /// <summary>
        /// Creates the file system from the user configuration (see <see cref="DavFS(Configuration, HttpMessageHandler)"/>).
        /// </summary>
        public DavFS(Configuration config) : this(config, null)
        {
        }

        /// <summary>
        /// Creates the file system from the user configuration: parses the server URL into server, host and (decoded) document
        /// library path, initializes <see cref="FileNode"/>, creates the HTTP and WebDAV clients (credentials, optional client
        /// certificate) and the metadata cache, then tests the connection by reading the library root.
        /// </summary>
        /// <param name="config">Mount configuration (server URL and type, credentials, flush and kernel cache modes, etc.).</param>
        /// <param name="TestHandler">Unit tests only: requests are sent to this handler (e.g. an in-memory server) instead of the network.</param>
        /// <exception cref="Exception">Thrown when the test request fails, with a dedicated message for invalid credentials (HTTP 401).</exception>
        internal DavFS(Configuration config, HttpMessageHandler TestHandler)
        {
            WebDAVMode webDAVMode = (WebDAVMode)config.ServerType;
            KernelCacheMode kernelCacheMode = (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), config.KernelCacheMode);

            this.MaxFileNodes = 500000;
            this.MaxFileSize = 0x7FFFFFC7; //Largest byte array .NET can allocate (file contents are held in a byte[]): bigger files are refused with STATUS_DISK_FULL instead of failing later
            //config.FlushMode is ignored: FlushAtCleanup is the only mode left (FlushAtWrite uploaded the whole file after every write)
            this.WebDAVMode = webDAVMode;
            this.kernelCacheMode = kernelCacheMode;
            this.MountAsNetworkDrive = config.MountAsNetworkDrive;

            var DavServerURI = new Uri(config.ServerURL);
            this.DAVServer = DavServerURI.GetLeftPart(UriPartial.Authority);
            this.DAVServeurAuthority = DavServerURI.DnsSafeHost;
            //Every path of the application is decoded (see WebDavItem.Path): only the WebDAV client encodes URLs
            String LibraryPath = Uri.UnescapeDataString(DavServerURI.AbsolutePath);
            this.DocumentLibraryPath = LibraryPath.Length > 1 ? LibraryPath.TrimEnd('/') : "";

            this.DAVLogin = config.ServerLogin;
            this.DAVPassword = config.ServerPassword;

            FileNode.Init(this.DocumentLibraryPath, this.WebDAVMode);

            if (TestHandler != null)
            {
                Http = WebDavClient.CreateHttpClient(TestHandler);
            }
            else
            {
                X509Certificate2 ClientCertificate = config.UseClientCertForAuthentication ? Tools.FindCertificate(config.CertStoreName, config.CertStoreLocation, config.CertSerial) : null;
                Http = WebDavClient.CreateHttpClient(new NetworkCredential(this.DAVLogin, this.DAVPassword), ClientCertificate);
                ServicePointManager.FindServicePoint(DavServerURI).ConnectionLimit = MaxConnectionsPerServer;
            }
            Client = new WebDavClient(Http, config.ServerURL);
            Cache = new CacheManager(CacheMode.Enabled, config.PreLoading, Client);

            //Test connection to server with the parameters entered in the configuration screen
            try
            {
                Client.PropFind(this.DocumentLibraryPath, 0, true).GetAwaiter().GetResult();
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                Http.Dispose();
                throw new Exception($"Cannot connect to server : Invalid credentials");
            }
            catch (Exception ex)
            {
                Http.Dispose();
                throw new Exception($"Cannot connect to server : {ex.Message}");
            }
        }

        /// <summary>
        /// WinFSP callback invoked before mounting: sets the general parameters of the file system on the host
        /// (kernel FileInfo cache timeout, optional network-drive prefix, sector size, case-insensitive search,
        /// no ACL/reparse point/named stream support, cleanup and flush behaviour).
        /// </summary>
        /// <param name="Host0">The <see cref="FileSystemHost"/> being configured.</param>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 Init(Object Host0)
        {
            FileSystemHost Host = (FileSystemHost)Host0;

            Host.FileInfoTimeout = unchecked((UInt32)(Int32)(this.kernelCacheMode));
            Host.FileSystemName = "davFS";
            if (MountAsNetworkDrive) Host.Prefix = $@"\{this.DAVServeurAuthority}\dav"; //mount as network drive
            Host.SectorSize = DavFS.MEMFS_SECTOR_SIZE;
            Host.SectorsPerAllocationUnit = DavFS.MEMFS_SECTORS_PER_ALLOCATION_UNIT;
            Host.VolumeCreationTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            Host.VolumeSerialNumber = (UInt32)(Host.VolumeCreationTime / (10000 * 1000));
            Host.CaseSensitiveSearch = false;
            Host.CasePreservedNames = true;
            Host.UnicodeOnDisk = true;
            Host.PersistentAcls = false;
            Host.ReparsePoints = false;
            Host.ReparsePointsAccessCheck = false;
            Host.NamedStreams = false;
            Host.PostCleanupWhenModifiedOnly = true; //Decide wheither to fire a cleanup message a every Create / open or only if the file was modified
            Host.FlushAndPurgeOnCleanup = true; //For Windows to trigger and flush when the last handle on the file is released

            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback returning volume information. The repository size is unknown, so total and free sizes are
        /// both reported as a fixed theoretical maximum (<c>MaxFileNodes * MaxFileSize</c>); the label is the one set by
        /// <see cref="SetVolumeLabel"/>.
        /// </summary>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 GetVolumeInfo(
            out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default(VolumeInfo);
            VolumeInfo.TotalSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.FreeSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.SetVolumeLabel(VolumeLabel);
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback that changes the volume label. The label is only kept in memory (not sent to the server).
        /// </summary>
        /// <returns>The result of <see cref="GetVolumeInfo"/> (always <c>STATUS_SUCCESS</c>).</returns>
        public override Int32 SetVolumeLabel(
            String VolumeLabel,
            out VolumeInfo VolumeInfo)
        {
            this.VolumeLabel = VolumeLabel;
            return GetVolumeInfo(out VolumeInfo);
        }

        /// <summary>
        /// WinFSP callback used to retrieve essential metadata about a file before it is opened, such as its attributes and security descriptor.
        /// <c>desktop.ini</c> and <c>autorun.inf</c> are always reported as missing. Otherwise the node is looked up in the cache;
        /// if unknown, it is fetched from the repository and added to the cache, and a missing element is recorded in the cache as
        /// non-existent so later lookups do not hit the server. On HTTP 401, <see cref="RepositoryAuthenticationFailed"/> is raised
        /// (once per mount).
        /// </summary>
        /// <param name="FileName">Local path of the file (relative to the volume root).</param>
        /// <param name="FileAttributes">Receives the file attributes.</param>
        /// <param name="SecurityDescriptor">If not null on input, receives the node's security descriptor.</param>
        /// <returns><c>STATUS_SUCCESS</c> if the element exists, <c>STATUS_OBJECT_NAME_NOT_FOUND</c> if it does not (or on 401 / WebDAV errors),
        /// <c>STATUS_NETWORK_UNREACHABLE</c> on HTTP transport errors.</returns>
        public override Int32 GetSecurityByName(
            String FileName,
            out UInt32 FileAttributes/* or ReparsePointIndex */,
            ref Byte[] SecurityDescriptor)
        {
            if (FileName.ToLower().Contains("desktop.ini") || (FileName.ToLower().Contains("autorun.inf")))
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            String OperationId = Guid.NewGuid().ToString();
            TraceStart(OperationId, FileName);

            var Lookup = LookupFileNode(FileName);
            if (Lookup.Status != STATUS_SUCCESS)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                TraceEnd(OperationId, null, $"{Lookup.Status} - {Lookup.Detail}");
                return Lookup.Status;
            }

            FileAttributes = Lookup.Node.FileInfo.FileAttributes;
            if (null != SecurityDescriptor) SecurityDescriptor = Lookup.Node.FileSecurity;
            TraceEnd(OperationId, Lookup.Node, $"STATUS_SUCCESS - {Lookup.Detail}");
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// Finds the node of a local path: from the cache, or else from the server (then cached). Elements that do not exist
        /// on the server are remembered as missing (see <see cref="CacheManager.AddMissingFileNoLock"/>).
        /// The cache lock is only held to read and update the cache, never during the request to the server: holding it there
        /// blocked every other file system operation (e.g. Explorer listing a folder) for the duration of each request,
        /// which froze Explorer while Office was saving a document (Office probes many temporary file names).
        /// </summary>
        /// <returns>The status (<c>STATUS_SUCCESS</c>, <c>STATUS_OBJECT_NAME_NOT_FOUND</c> — also on 401, which raises
        /// <see cref="RepositoryAuthenticationFailed"/> — or <c>STATUS_NETWORK_UNREACHABLE</c> on a transport error), the node on
        /// success, and a short description for the logs.</returns>
        private (Int32 Status, FileNode Node, String Detail) LookupFileNode(String FileName)
        {
            Cache.Lock();
            try
            {
                var KnownNode = Cache.GetFileNodeNoLock(FileName);
                if (KnownNode.node != null) return (STATUS_SUCCESS, KnownNode.node, "From Cache");
                if (KnownNode.IsNonExistent) return (STATUS_OBJECT_NAME_NOT_FOUND, null, "Known to be non-existent");
                //Not recorded as missing on purpose: Create still checks the server before uploading (see ElementExists)
                if (Cache.IsMissingFromFreshFolderListingNoLock(FileName)) return (STATUS_OBJECT_NAME_NOT_FOUND, null, "Not in the folder listing");
            }
            finally
            {
                Cache.Unlock();
            }

            WebDavItem RepositoryElement;
            try
            {
                var Proxy = Client;
                RepositoryElement = GetRepositoryElement(FileName);
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                OnAuthenticationFailed();
                return (STATUS_OBJECT_NAME_NOT_FOUND, null, "401");
            }
            catch (HttpRequestException ex)
            {
                return (STATUS_NETWORK_UNREACHABLE, null, ex.Message); //Not remembered as missing: it may exist
            }
            catch (Exception ex)
            {
                AddMissingFile(FileName);
                return (STATUS_OBJECT_NAME_NOT_FOUND, null, ex.Message);
            }

            if (RepositoryElement == null)
            {
                AddMissingFile(FileName);
                return (STATUS_OBJECT_NAME_NOT_FOUND, null, "Not found on the server");
            }

            Cache.Lock();
            try
            {
                //Another operation may have cached the same element meanwhile: the cached instance is returned in that case
                return (STATUS_SUCCESS, Cache.AddFileNodeNoLock(new FileNode(RepositoryElement)), "From Repository");
            }
            finally
            {
                Cache.Unlock();
            }
        }

        /// <summary>
        /// Returns whether an element exists at a local path. When the cache knows the path is missing (e.g. Office probed its
        /// temporary file name just before), no request is sent; otherwise the server is asked. Used by <see cref="Create"/> and
        /// <see cref="Rename"/>, which run while WinFSP holds a lock on the parent folder or on the volume: every request avoided
        /// there shortens the time Explorer is blocked. Errors (401, network...) are propagated to the caller.
        /// </summary>
        /// <param name="TrustFolderListing">Also answer "missing" when the name is absent from a fresh listing of its folder
        /// (see <see cref="CacheManager.IsMissingFromFreshFolderListingNoLock"/>). Only for Rename, whose MOVE is sent with
        /// "Overwrite: F" and therefore never replaces an element created meanwhile by another client; Create uploads with a PUT,
        /// which would replace it, so it relies only on names explicitly found missing on the server.</param>
        private bool ElementExists(String FileName, WebDavClient Proxy, bool TrustFolderListing)
        {
            Cache.Lock();
            try
            {
                if (Cache.GetFileNodeNoLock(FileName).IsNonExistent) return false;
                if (TrustFolderListing && Cache.IsMissingFromFreshFolderListingNoLock(FileName)) return false;
            }
            finally
            {
                Cache.Unlock();
            }

            return Proxy.Exists(FileNode.ConvertLocalPathToRepositoryPath(FileName)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// How long the content of a closed file is kept in memory to be reused by a new open (Word, for instance, reopens the
        /// same document several times while saving it: each reopen used to download it again).
        /// </summary>
        private const Int32 ClosedContentMaxAgeInSeconds = 15;

        /// <summary>
        /// A Flush coming less than this delay after the last successful upload of the file does not upload it again (see <see cref="Flush"/>).
        /// </summary>
        private const Int32 FlushMinimumIntervalInMilliseconds = 1000;

        /// <summary>
        /// Maximum memory used by the contents kept after their file was closed; the oldest are released first.
        /// </summary>
        private const Int64 ClosedContentMaxTotalBytes = 64L * 1024 * 1024;

        private readonly object RetainedContentsLock = new object();

        /// <summary>
        /// Closed nodes whose content is kept in memory, oldest first.
        /// </summary>
        private readonly List<FileNode> RetainedContents = new List<FileNode>();

        /// <summary>
        /// Keeps the content of a node whose last handle was just closed (the content being identical to the server), so that
        /// a new open within <see cref="ClosedContentMaxAgeInSeconds"/> does not download it again. Releases the contents that
        /// expired or exceed <see cref="ClosedContentMaxTotalBytes"/>. Must be called with the node's <c>OperationLock</c> held.
        /// </summary>
        private void RetainClosedContent(FileNode CFN)
        {
            CFN.ContentRetainedAt = DateTime.Now;
            var ToRelease = new List<FileNode>();

            lock (RetainedContentsLock)
            {
                RetainedContents.Remove(CFN);
                RetainedContents.Add(CFN);

                //From the newest to the oldest: keep what is recent and fits in the memory budget
                DateTime Now = DateTime.Now;
                Int64 Total = 0;
                for (int i = RetainedContents.Count - 1; i >= 0; i--)
                {
                    FileNode Node = RetainedContents[i];
                    Byte[] Data = Node.FileData;
                    Int64 Size = Data?.LongLength ?? 0;
                    if (Data == null || (Now - Node.ContentRetainedAt).TotalSeconds > ClosedContentMaxAgeInSeconds || Total + Size > ClosedContentMaxTotalBytes)
                    {
                        ToRelease.Add(Node);
                        RetainedContents.RemoveAt(i);
                    }
                    else
                    {
                        Total += Size;
                    }
                }
            }

            foreach (FileNode Node in ToRelease)
            {
                if (Object.ReferenceEquals(Node, CFN))
                {
                    ReleaseClosedContent(Node); //Its lock is already held by the caller
                }
                else if (Monitor.TryEnter(Node.OperationLock)) //Never wait for another node: it is busy, so it is in use anyway
                {
                    try
                    {
                        ReleaseClosedContent(Node);
                    }
                    finally
                    {
                        Monitor.Exit(Node.OperationLock);
                    }
                }
            }
        }

        /// <summary>
        /// Frees the content of a node if it is still closed and has nothing to upload.
        /// </summary>
        private static void ReleaseClosedContent(FileNode Node)
        {
            if (Node.OpenCount == 0 && !Node.HasUnflushedData) Node.FileData = null;
        }

        /// <summary>
        /// Describes the element at a local path on the server (see <see cref="WebDavClient.GetItem"/>). The library root gets an
        /// empty display name.
        /// </summary>
        /// <returns>The element, or null if it does not exist.</returns>
        private WebDavItem GetRepositoryElement(String LocalFileName)
        {
            String RepositoryPath = FileNode.ConvertLocalPathToRepositoryPath(LocalFileName);
            WebDavItem Element = Client.GetItem(RepositoryPath).GetAwaiter().GetResult();
            if (Element != null && FileNode.IsRepositoryRootPath(RepositoryPath)) Element.DisplayName = "";
            return Element;
        }

        /// <summary>
        /// Remembers that a path does not exist on the server, taking the cache lock.
        /// </summary>
        private void AddMissingFile(String FileName)
        {
            Cache.Lock();
            try
            {
                Cache.AddMissingFileNoLock(FileName);
            }
            finally
            {
                Cache.Unlock();
            }
        }

        /// <summary>
        /// WinFSP callback that opens an existing file or directory. The node is taken from the cache or, if unknown, fetched from the
        /// repository and cached (missing elements are cached as non-existent). The node's open handle count is incremented.
        /// File contents are not downloaded here; they are downloaded lazily by <see cref="Read"/>.
        /// On HTTP 401, <see cref="RepositoryAuthenticationFailed"/> is raised (once per mount).
        /// </summary>
        /// <param name="FileName">Local path of the file to open.</param>
        /// <param name="FileNode0">Receives the <see cref="FileNode"/> used as file context by subsequent callbacks.</param>
        /// <param name="FileDesc">Unused (always null).</param>
        /// <param name="FileInfo">Receives the file information.</param>
        /// <param name="NormalizedName">Receives the file name unchanged.</param>
        /// <returns><c>STATUS_SUCCESS</c>, <c>STATUS_OBJECT_NAME_NOT_FOUND</c> (missing element, 401 or WebDAV error) or
        /// <c>STATUS_NETWORK_UNREACHABLE</c> on HTTP transport errors.</returns>
        public override Int32 Open(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            out Object FileNode0,
            out Object FileDesc,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, FileName);

            FileNode0 = default(Object);
            FileDesc = default(Object);
            FileInfo = default(FileInfo);
            NormalizedName = default(String);

            //The cache lock is not held during the request to the server (see LookupFileNode)
            var Lookup = LookupFileNode(FileName);
            if (Lookup.Status != STATUS_SUCCESS)
            {
                DebugEnd(OperationId, null, $"{Lookup.Status} - {Lookup.Detail}");
                return Lookup.Status;
            }

            Int32 HandleCount = Interlocked.Increment(ref Lookup.Node.OpenCount);

            //First handle on a file whose content was kept in memory after its last close: reuse it only if it is still valid
            //(recent, and the file did not change on the server since), otherwise the next read downloads it again
            if (HandleCount == 1 && Lookup.Node.FileData != null)
            {
                lock (Lookup.Node.OperationLock)
                {
                    if (!Lookup.Node.HasUnflushedData && !Lookup.Node.CanReuseContent(ClosedContentMaxAgeInSeconds)) Lookup.Node.FileData = null;
                }
            }
            DebugEnd(OperationId, Lookup.Node, $"STATUS_SUCCESS - {Lookup.Detail} - Handle {HandleCount}");

            FileNode0 = Lookup.Node;
            FileInfo = Lookup.Node.FileInfo;
            NormalizedName = FileName;
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback returning the cached file information of an open node. No server call is made.
        /// </summary>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 GetFileInfo(
            Object FileNode0,
            Object FileDesc,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                FileInfo = CFN.FileInfo;

                String OperationId = Guid.NewGuid().ToString();
                TraceStart(OperationId, CFN);
                TraceEnd(OperationId, CFN, "STATUS_SUCCESS");
            }

            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback returning the security descriptor stored on the open node. No server call is made.
        /// </summary>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 GetSecurity(
            Object FileNode0,
            Object FileDesc,
            ref Byte[] SecurityDescriptor)
        {
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                TraceStart(OperationId, CFN);

                SecurityDescriptor = CFN.FileSecurity;

                TraceEnd(OperationId, CFN, "STATUS_SUCCESS");
            }
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback that enumerates a directory one entry per call. On the first call (<paramref name="Context"/> is null) the folder
        /// content is obtained from <see cref="CacheManager.GetFolderContent"/> (which may query the server) starting after
        /// <paramref name="Marker"/>, and an enumerator is stored in <paramref name="Context"/>; subsequent calls advance that enumerator.
        /// <paramref name="Pattern"/> is not applied. If the listing fails with a 401, <see cref="RepositoryAuthenticationFailed"/> is raised
        /// (once per mount).
        /// </summary>
        /// <param name="FileNode0">The directory node.</param>
        /// <param name="Context">Enumeration state (<see cref="DirectoryEnumeratorContext"/>) kept between calls.</param>
        /// <param name="FileName">Receives the entry name.</param>
        /// <param name="FileInfo">Receives the entry file information.</param>
        /// <returns><c>true</c> if an entry was returned; <c>false</c> at the end of the enumeration or on error.</returns>
        public override Boolean ReadDirectoryEntry(
            Object FileNode0,
            Object FileDesc,
            String Pattern,
            String Marker, //Determines from which file in the directory reading starts (exclusive) (null = start from the beginning)
            ref Object Context,
            out String FileName,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;
            IEnumerator<Tuple<String, FileNode>> Enumerator;
            Guid OperationId;

            if (Context == null)
            {
                Enumerator = null;
                OperationId = Guid.NewGuid();
                DebugStart(OperationId.ToString(), CFN);

                List<Tuple<String, FileNode>> ChildrenFileNames = null;
                var Result = Cache.GetFolderContent(CFN, Marker);
                if (!Result.Success)
                {
                    if (Result.ErrorMessage == "401")
                    {
                        OnAuthenticationFailed();
                    }
                    DebugEnd(OperationId.ToString(), CFN, $"Exception : {Result.ErrorMessage}");
                    FileName = default(String);
                    FileInfo = default(FileInfo);
                    return false;
                }
                else
                {
                    ChildrenFileNames = Result.Content;
                }

                Enumerator = ChildrenFileNames.GetEnumerator();
                Context = new DirectoryEnumeratorContext() { Enumerator = Enumerator, OperationId = OperationId };
            }
            else
            {
                Enumerator = ((DirectoryEnumeratorContext)Context).Enumerator;
                OperationId = ((DirectoryEnumeratorContext)Context).OperationId;
            }

            if (Enumerator.MoveNext())
            {
                Tuple<String, FileNode> CurrentCFN = Enumerator.Current;
                FileName = CurrentCFN.Item1;
                FileInfo = CurrentCFN.Item2.FileInfo;
                return true;
            }

            DebugEnd(OperationId.ToString(), CFN, "STATUS_SUCCESS");

            FileName = default(String);
            FileInfo = default(FileInfo);
            return false;
        }


        /// <summary>
        /// WinFSP callback asking whether a file or directory may be deleted (the actual deletion on the server happens in
        /// <see cref="Cleanup"/> when the delete flag is set). A file can always be deleted. A directory can only be deleted when
        /// it is empty, like on NTFS: a WebDAV DELETE on a collection deletes its whole content, so deleting a non-empty directory
        /// (e.g. RemoveDirectory called by an application) would silently delete everything in it. The server is asked, not the
        /// cache, which may miss elements added by other clients; elements created on the drive and not uploaded yet count too.
        /// </summary>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_DIRECTORY_NOT_EMPTY</c>; <c>STATUS_NETWORK_UNREACHABLE</c> on transport errors or 401
        /// (which also raises <see cref="RepositoryAuthenticationFailed"/>); <c>STATUS_ACCESS_DENIED</c> if the content cannot be checked.</returns>
        public override Int32 CanDelete(
            Object FileNode0,
            Object FileDesc,
            String FileName)
        {
            FileNode CFN = (FileNode)FileNode0;
            if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0) return STATUS_SUCCESS;

            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, CFN);

            Cache.Lock();
            try
            {
                if (Cache.HasPendingChildrenNoLock(CFN.LocalPath))
                {
                    DebugEnd(OperationId, CFN, "STATUS_DIRECTORY_NOT_EMPTY - Element not uploaded yet");
                    return STATUS_DIRECTORY_NOT_EMPTY;
                }
            }
            finally
            {
                Cache.Unlock();
            }

            try
            {
                var Items = Client.PropFind(CFN.RepositoryPath, 1, true).GetAwaiter().GetResult();
                bool HasChildren = Items.Any(Item => !FileNode.IsSameRepositoryPath(Item.Path, CFN.RepositoryPath));
                DebugEnd(OperationId, CFN, HasChildren ? "STATUS_DIRECTORY_NOT_EMPTY" : "STATUS_SUCCESS");
                return HasChildren ? STATUS_DIRECTORY_NOT_EMPTY : STATUS_SUCCESS;
            }
            catch (WebDavException ex) when (ex.StatusCode == 404)
            {
                DebugEnd(OperationId, CFN, "STATUS_SUCCESS - Already deleted on the server");
                return STATUS_SUCCESS;
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                OnAuthenticationFailed();
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (HttpRequestException)
            {
                DebugEnd(OperationId, CFN, "STATUS_NETWORK_UNREACHABLE");
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (Exception ex)
            {
                //When in doubt, do not delete: the deletion of a collection is recursive on the server
                DebugEnd(OperationId, CFN, $"STATUS_ACCESS_DENIED - {ex.Message}");
                return STATUS_ACCESS_DENIED;
            }
        }

        /// <summary>
        /// WinFSP callback that creates a new file or directory directly on the server. It first checks that the element does not
        /// already exist, then either uploads an empty file (marked as having unflushed data) or creates the folder, re-reads the
        /// created element from the repository (see <see cref="GetCreatedFileNode"/>), adds it to the cache and increments its open handle count.
        /// <paramref name="AllocationSize"/>, <paramref name="SecurityDescriptor"/> and <paramref name="CreateOptions"/> are ignored.
        /// On HTTP 401, <see cref="RepositoryAuthenticationFailed"/> is raised (once per mount).
        /// </summary>
        /// <param name="FileName">Local path of the element to create.</param>
        /// <param name="FileAttributes">Attributes requested; only the Directory flag is used to choose between file and folder creation.</param>
        /// <param name="FileNode0">Receives the new <see cref="FileNode"/>.</param>
        /// <param name="FileInfo">Receives the new element's file information.</param>
        /// <param name="NormalizedName">Receives the file name unchanged.</param>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_OBJECT_NAME_COLLISION</c> if the element exists; <c>STATUS_ACCESS_DENIED</c> on a WebDAV
        /// conflict or unexpected error; <c>STATUS_CANNOT_MAKE</c> if the server refuses the creation; <c>STATUS_NETWORK_UNREACHABLE</c>
        /// on transport errors or 401.</returns>
        public override Int32 Create(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            UInt32 FileAttributes,
            Byte[] SecurityDescriptor,
            UInt64 AllocationSize,
            out Object FileNode0,
            out Object FileDesc,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            String OperationId = Guid.NewGuid().ToString();
            DebugStart(OperationId, FileName);

            LogListItem L;

            FileNode0 = default(Object);
            FileDesc = default(Object);
            FileInfo = default(FileInfo);
            NormalizedName = default(String);

            FileNode CFN;
            var Proxy = Client;

            try
            {
                //WinFSP holds a lock on the parent folder during Create (Explorer cannot list it meanwhile): skip the existence check
                //when the cache already knows the name is free (it was probed just before, which is what Office and Explorer do)
                if (ElementExists(FileName, Proxy, false))
                {
                    DebugEnd(OperationId, null, "STATUS_OBJECT_NAME_COLLISION");
                    return STATUS_OBJECT_NAME_COLLISION;
                }
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                OnAuthenticationFailed();
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (HttpRequestException)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_NETWORK_UNREACHABLE" };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, null, "STATUS_NETWORK_UNREACHABLE");
                return STATUS_NETWORK_UNREACHABLE;
            }
            catch (Exception)
            {
                FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_CANNOT_MAKE" };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, null, "STATUS_CANNOT_MAKE");
                return FileSystemBase.STATUS_CANNOT_MAKE;
            }

            if ((FileAttributes & (UInt32)System.IO.FileAttributes.Directory) == 0)
            {
                //The file is created on the server by its first upload (Flush, Cleanup or Close), not right away with an empty
                //content (see FileNode.IsNewOnServer). It is marked as having unflushed data so that it is created even if nothing
                //is written in it
                CFN = GetCreatedFileNode(Proxy, FileName, false);
                CFN.IsNewOnServer = true;
                CFN.HasUnflushedData = true;
                CFN.FileData = new byte[0]; //Loaded and empty: nothing to download
            }
            else
            {
                try
                {
                    Proxy.MkCol(FileNode.ConvertLocalPathToRepositoryPath(FileName)).GetAwaiter().GetResult(); //Throws on failure
                    CFN = GetCreatedFileNode(Proxy, FileName, true);
                }
                catch (WebDavException ex) when (ex.StatusCode == 409)
                {
                    //Seems that we get Conflict response when the folder cannot be created
                    //As we do conflict checking before running the CreateDir command, we consider it as a permission issue
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_ACCESS_DENIED" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                    return STATUS_ACCESS_DENIED;
                }
                catch (WebDavException ex) when (ex.StatusCode == 401)
                {
                    OnAuthenticationFailed();
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (WebDavException)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_CANNOT_MAKE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_CANNOT_MAKE");
                    return STATUS_CANNOT_MAKE;
                }
                catch (HttpRequestException)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_NETWORK_UNREACHABLE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_NETWORK_UNREACHABLE");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_ACCESS_DENIED" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                    return STATUS_ACCESS_DENIED;
                }
            }

            Cache.Lock();
            try
            {
                //The element did not exist on the server before this creation: a node still cached for this path is stale, replace it
                Cache.DeleteFileEntry(CFN.LocalPath);
                Cache.AddFileNodeNoLock(CFN);
            }
            finally
            {
                Cache.Unlock(); //Never leave the cache locked: it would freeze the whole drive
            }

            Interlocked.Increment(ref CFN.OpenCount);
            FileNode0 = CFN;
            FileInfo = CFN.FileInfo;
            NormalizedName = FileName;

            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Create", File = FileName, Result = "STATUS_SUCCESS" };
            RepositoryActionPerformed?.Invoke(this, L);
            DebugEnd(OperationId, null, "STATUS_SUCCESS");

            return STATUS_SUCCESS;

            /*
            FileNode FileNode;
            FileNode ParentNode;
            Int32 Result = STATUS_SUCCESS;

            FileNode = FileNodeMap.Get(FileName);
            if (null != FileNode)
                return STATUS_OBJECT_NAME_COLLISION;
            ParentNode = FileNodeMap.GetParent(FileName, ref Result);
            if (null == ParentNode)
                return Result;

            if (0 != (CreateOptions & FILE_DIRECTORY_FILE))
                AllocationSize = 0;
            if (FileNodeMap.Count() >= MaxFileNodes)
                return STATUS_CANNOT_MAKE;
            if (AllocationSize > MaxFileSize)
                return STATUS_DISK_FULL;

            if ("\\" != ParentNode.FileName) FileName = ParentNode.FileName + "\\" + Path.GetFileName(FileName); //normalize name
            FileNode = new FileNode(FileName);
            FileNode.MainFileNode = FileNodeMap.GetMain(FileName);
            FileNode.FileInfo.FileAttributes = 0 != (FileAttributes & (UInt32)System.IO.FileAttributes.Directory) ?
                FileAttributes : FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
            FileNode.FileSecurity = SecurityDescriptor;
            if (0 != AllocationSize)
            {
                Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                if (0 > Result)
                    return Result;
            }
            FileNodeMap.Insert(FileNode);

            Interlocked.Increment(ref FileNode.OpenCount);
            FileNode0 = FileNode;
            FileInfo = FileNode.GetFileInfo();
            NormalizedName = FileNode.FileName;
            */
        }

        /// <summary>
        /// WinFSP callback invoked when an existing file is opened with overwrite/supersede semantics. Discards the in-memory
        /// content (nothing is downloaded), allocates an empty buffer of <paramref name="AllocationSize"/> bytes, replaces or merges the attributes (always adding Archive), resets the
        /// file size to 0 and updates the timestamps. Nothing is sent to the server here: the node is marked as having unflushed data,
        /// so the (possibly empty) new content is uploaded at <see cref="Cleanup"/>/<see cref="Close"/>.
        /// </summary>
        /// <returns><c>STATUS_SUCCESS</c>, the error returned by <see cref="SetFileSizeInternal"/>, or <c>STATUS_UNEXPECTED_IO_ERROR</c> on exception.</returns>
        public override Int32 Overwrite(
            Object FileNode0,
            Object FileDesc,
            UInt32 FileAttributes,
            Boolean ReplaceFileAttributes,
            UInt64 AllocationSize,
            out FileInfo FileInfo)
        {
            FileInfo = default(FileInfo);
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                try
                {
                    //The previous content is discarded: start from an empty file instead of downloading or copying the old content
                    CFN.FileData = new byte[0]; //Loaded and empty (null would mean "not loaded" and the old content would be downloaded again)
                    CFN.FileInfo.FileSize = 0;
                    CFN.FileInfo.AllocationSize = 0;

                    Int32 Result = SetFileSizeInternal(CFN, AllocationSize, true);
                    if (Result < 0)
                    {
                        DebugEnd(OperationId, CFN, Result.ToString());
                        return Result;
                    }

                    if (ReplaceFileAttributes) CFN.FileInfo.FileAttributes = FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                    else CFN.FileInfo.FileAttributes |= FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                    CFN.FileInfo.FileSize = 0;
                    CFN.FileInfo.LastAccessTime =
                    CFN.FileInfo.LastWriteTime =
                    CFN.FileInfo.ChangeTime = (UInt64)DateTime.Now.ToFileTimeUtc();
                    CFN.HasUnflushedData = true; //The file is now empty: this must reach the server even if nothing is written afterwards

                    FileInfo = CFN.FileInfo;

                    /*
                    FileInfo = default(FileInfo);

                    FileNode FileNode = (FileNode)FileNode0;
                    Int32 Result;

                    List<String> StreamFileNames = new List<String>(FileNodeMap.GetStreamFileNames(FileNode));
                    foreach (String StreamFileName in StreamFileNames)
                    {
                        FileNode StreamNode = FileNodeMap.Get(StreamFileName);
                        if (null == StreamNode)
                            continue; // should not happen
                        if (0 == StreamNode.OpenCount)
                            FileNodeMap.Remove(StreamNode);
                    }

                    Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                    if (0 > Result)
                        return Result;
                    if (ReplaceFileAttributes)
                        FileNode.FileInfo.FileAttributes = FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                    else
                        FileNode.FileInfo.FileAttributes |= FileAttributes | (UInt32)System.IO.FileAttributes.Archive;
                    FileNode.FileInfo.FileSize = 0;
                    FileNode.FileInfo.LastAccessTime =
                    FileNode.FileInfo.LastWriteTime =
                    FileNode.FileInfo.ChangeTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                    FileInfo = FileNode.GetFileInfo();
                    */

                    DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                    return STATUS_SUCCESS;
                }
                catch (Exception ex)
                {
                    DebugEnd(OperationId, CFN, $"Exception {ex.Message}");
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
        }

        /// <summary>
        /// WinFSP callback invoked when a file handle is closed. Whatever the flush mode, pending changes are uploaded
        /// (see <see cref="UploadPendingChanges"/>). The open handle count is then decremented and, when it reaches zero, the
        /// in-memory content is kept for a short time if it is identical to the server (see <see cref="RetainClosedContent"/>),
        /// or released if some changes could not be uploaded.
        /// </summary>
        public override void Close(
            Object FileNode0,
            Object FileDesc)
        {
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                //Last chance to upload pending changes (whatever the flush mode). On failure, the handle is still released below
                UploadPendingChanges(CFN, "Close Flush", OperationId);

                Int32 HandleCount = Interlocked.Decrement(ref CFN.OpenCount);
                if (HandleCount == 0)
                {
                    if (CFN.HasUnflushedData)
                    {
                        //Changes that could not be uploaded are only kept in the local recovery copy: the content is released and
                        //the flag cleared so that the node can be refreshed from the server again
                        CFN.FileData = null;
                        CFN.HasUnflushedData = false;
                    }
                    else if (CFN.FileData != null)
                    {
                        //The content is identical to the server: keep it for a short time, applications (Word...) often reopen
                        //the file right away and would otherwise download it again (see RetainClosedContent)
                        RetainClosedContent(CFN);
                    }
                }
                DebugEnd(OperationId, CFN, $"STATUS_SUCCESS  - Handle {HandleCount}");
            }
        }

        /// <summary>
        /// WinFSP callback invoked when the last handle of a file object is cleaned up (before <see cref="Close"/>). Does nothing for the
        /// repository root. Depending on <paramref name="Flags"/>:
        /// <list type="bullet">
        /// <item><c>CleanupSetAllocationSize</c>: shrinks the in-memory allocation to the file size rounded up to the allocation unit.</item>
        /// <item><c>CleanupDelete</c>: deletes the file or folder on the server, removes it from the cache and marks the node as deleted.</item>
        /// <item>Otherwise: uploads the in-memory content if the node has unflushed data, whatever the flags and the flush mode
        /// (see <see cref="UploadPendingChanges"/>).</item>
        /// </list>
        /// Results are reported through <see cref="RepositoryActionPerformed"/>.
        /// </summary>
        public override void Cleanup(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            UInt32 Flags)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;
            LogListItem L;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                if (FileNode.IsRepositoryRootPath(CFN.RepositoryPath)) return;

                if ((Flags & CleanupSetAllocationSize) != 0)
                {
                    UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                    UInt64 AllocationSize = (CFN.FileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
                    SetFileSizeInternal(CFN, AllocationSize, true);
                }

                if ((Flags & CleanupDelete) != 0 && CFN.IsNewOnServer)
                {
                    //Created on the drive and never uploaded: it does not exist on the server, there is nothing to delete there
                    Cache.DeleteFileNode(CFN);
                    CFN.IsDeleted = true;
                    CFN.HasUnflushedData = false;
                    CFN.IsNewOnServer = false;
                    RepositoryActionPerformed?.Invoke(this, new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_SUCCESS" });
                    DebugEnd(OperationId, null, "STATUS_SUCCESS - Never uploaded");
                }
                else if ((Flags & CleanupDelete) != 0)
                {
                    var Proxy = Client;
                    if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
                    {
                        try
                        {
                            //File
                            Proxy.Delete(CFN.RepositoryPath, false).GetAwaiter().GetResult();
                            Cache.DeleteFileNode(CFN);
                            CFN.IsDeleted = true;
                            CFN.HasUnflushedData = false;
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_SUCCESS");
                        }
                        catch (Exception ex)
                        {
                            if (IsAuthenticationError(ex)) OnAuthenticationFailed();
                            RepositoryActionPerformed?.Invoke(this, new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_FAILED" });
                            DebugEnd(OperationId, CFN, $"Exception : {ex.Message}");
                        }
                    }
                    else
                    {
                        try
                        {
                            //Directory
                            Proxy.Delete(CFN.RepositoryPath, true).GetAwaiter().GetResult();
                            Cache.DeleteFileNode(CFN);
                            CFN.IsDeleted = true;
                            CFN.HasUnflushedData = false;
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_SUCCESS");
                        }
                        catch (Exception ex)
                        {
                            if (IsAuthenticationError(ex)) OnAuthenticationFailed();
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_FAILED" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_FAILED");
                        }
                    }
                }
                else
                {
                    //Upload pending changes whatever the cleanup flags: the flags only tell which metadata changed, while
                    //HasUnflushedData tells whether the content differs from the server (writes, truncation, overwrite)
                    UploadPendingChanges(CFN, "Cleanup Flush", OperationId);
                }

                /*
                FileNode FileNode = (FileNode)FileNode0;

                FileNode MainFileNode = null != FileNode.MainFileNode ?
                    FileNode.MainFileNode : FileNode;

                if (0 != (Flags & CleanupSetArchiveBit))
                {
                    if (0 == (MainFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                        MainFileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.Archive;
                }

                if (0 != (Flags & (CleanupSetLastAccessTime | CleanupSetLastWriteTime | CleanupSetChangeTime)))
                {
                    UInt64 SystemTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                    if (0 != (Flags & CleanupSetLastAccessTime))
                        MainFileNode.FileInfo.LastAccessTime = SystemTime;
                    if (0 != (Flags & CleanupSetLastWriteTime))
                        MainFileNode.FileInfo.LastWriteTime = SystemTime;
                    if (0 != (Flags & CleanupSetChangeTime))
                        MainFileNode.FileInfo.ChangeTime = SystemTime;
                }

                if (0 != (Flags & CleanupSetAllocationSize))
                {
                    UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                    UInt64 AllocationSize = (FileNode.FileInfo.FileSize + AllocationUnit - 1) /
                        AllocationUnit * AllocationUnit;
                    SetFileSizeInternal(FileNode, AllocationSize, true);
                }

                if (0 != (Flags & CleanupDelete) && !FileNodeMap.HasChild(FileNode))
                {
                    List<String> StreamFileNames = new List<String>(FileNodeMap.GetStreamFileNames(FileNode));
                    foreach (String StreamFileName in StreamFileNames)
                    {
                        FileNode StreamNode = FileNodeMap.Get(StreamFileName);
                        if (null == StreamNode)
                            continue; // should not happen
                        FileNodeMap.Remove(StreamNode);
                    }
                    FileNodeMap.Remove(FileNode);
                }
                */
            }
        }

        /// <summary>
        /// WinFSP callback that reads file content. On the first read of an open node (<c>FileData</c> is null) the whole file is downloaded
        /// from the server and kept in memory; the requested range is then copied from memory into <paramref name="Buffer"/>.
        /// On HTTP 401, <see cref="RepositoryAuthenticationFailed"/> is raised (once per mount).
        /// </summary>
        /// <param name="Buffer">Destination buffer.</param>
        /// <param name="Offset">Offset in the file where reading starts.</param>
        /// <param name="Length">Number of bytes requested.</param>
        /// <param name="BytesTransferred">Receives the number of bytes copied.</param>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_END_OF_FILE</c> if <paramref name="Offset"/> is past the end of the file (<c>FileInfo.FileSize</c>);
        /// <c>STATUS_NETWORK_UNREACHABLE</c> if the download fails (including 401).</returns>
        public override Int32 Read(
            Object FileNode0,
            Object FileDesc,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            out UInt32 BytesTransferred)
        {
            BytesTransferred = default(UInt32);
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                Int32 LoadResult = EnsureContentLoaded(CFN, true);
                if (LoadResult < 0)
                {
                    DebugEnd(OperationId, CFN, $"Cannot load content : {LoadResult}");
                    return LoadResult;
                }

                if (CFN.FileData == null)
                {
                    //Defensive: should not happen after a download
                    DebugEnd(OperationId, CFN, "STATUS_END_OF_FILE");
                    return STATUS_END_OF_FILE;
                }

                //The buffer can be larger than the file (it is rounded up to the allocation size after a write or resize):
                //the logical end of the file is FileInfo.FileSize, the buffer length is only an upper bound
                UInt64 FileSize = Math.Min(CFN.FileInfo.FileSize, (UInt64)CFN.FileData.LongLength);

                if (Offset >= FileSize)
                {
                    BytesTransferred = default(UInt32);
                    DebugEnd(OperationId, CFN, "STATUS_END_OF_FILE");
                    return STATUS_END_OF_FILE;
                }

                UInt64 EndOffset = Offset + Length;
                if (EndOffset > FileSize) EndOffset = FileSize;

                BytesTransferred = (UInt32)(EndOffset - Offset);
                Marshal.Copy(CFN.FileData, (int)Offset, Buffer, (int)BytesTransferred);

                /*
                FileNode FileNode = (FileNode)FileNode0;
                UInt64 EndOffset;

                if (Offset >= FileNode.FileInfo.FileSize)
                {
                    BytesTransferred = default(UInt32);
                    return STATUS_END_OF_FILE;
                }

                EndOffset = Offset + Length;
                if (EndOffset > FileNode.FileInfo.FileSize)
                    EndOffset = FileNode.FileInfo.FileSize;

                BytesTransferred = (UInt32)(EndOffset - Offset);
                Marshal.Copy(FileNode.FileData, (int)Offset, Buffer, (int)BytesTransferred);
                */

                //LogSuccess($"{CFN.handle} Read {CFN.LocalPath} from {Offset} for {BytesTransferred} bytes | requested {Length} bytes");
                DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                return STATUS_SUCCESS;
            }
        }

        /// <summary>
        /// WinFSP callback that writes data into the in-memory content of the file. With <paramref name="ConstrainedIo"/> the write is
        /// truncated to the current file size; otherwise the file is grown as needed (and <paramref name="WriteToEndOfFile"/> appends).
        /// The node is marked as having unflushed data, uploaded by the next <see cref="Flush"/>, <see cref="Cleanup"/> or <see cref="Close"/>. If the content is not loaded yet, it is downloaded first so that the bytes outside
        /// the written range are preserved.
        /// </summary>
        /// <param name="Buffer">Source buffer.</param>
        /// <param name="Offset">Offset in the file where writing starts (ignored if <paramref name="WriteToEndOfFile"/> is set).</param>
        /// <param name="Length">Number of bytes to write.</param>
        /// <param name="BytesTransferred">Receives the number of bytes written.</param>
        /// <param name="FileInfo">Receives the updated file information.</param>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_UNEXPECTED_IO_ERROR</c> if the node is deleted, resizing or copying fails, or the upload
        /// fails; <c>STATUS_ACCESS_DENIED</c> on a WebDAV conflict during upload; <c>STATUS_NETWORK_UNREACHABLE</c> on transport errors,
        /// on 401, or if the current content cannot be downloaded.</returns>
        public override Int32 Write(
            Object FileNode0,
            Object FileDesc,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            Boolean WriteToEndOfFile,
            Boolean ConstrainedIo,
            out UInt32 BytesTransferred,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            BytesTransferred = default(UInt32);
            FileInfo = default(FileInfo);
            UInt64 EndOffset;

            LogListItem L;
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                if (CFN.IsDeleted) return STATUS_UNEXPECTED_IO_ERROR;

                //The bytes that are not overwritten must be kept: load the current content before writing into it
                Int32 LoadResult = EnsureContentLoaded(CFN);
                if (LoadResult < 0)
                {
                    DebugEnd(OperationId, CFN, $"Cannot load content : {LoadResult}");
                    return LoadResult;
                }

                /*
                CFN.FileData = null;
                byte[] RepositoryContent = CFN.GetContent(Proxy);
                CFN.FileData = new byte[(int)CFN.FileInfo.FileSize];
                if (CFN.FileData == null)
                {
                    BytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    return STATUS_SUCCESS;
                }
                try
                {
                    Array.Copy(RepositoryContent, CFN.FileData, Math.Min((int)CFN.FileInfo.FileSize, RepositoryContent.Length));
                }
                catch (Exception ex)
                {
                    LogTrace($"{CFN.handle} Write Exception {ex.Message}");
                }
                */

                if (ConstrainedIo)
                {
                    //ContrainedIo - we cannot increase the file size so EndOffset will always be at maximum equal to CFN.FileInfo.FileSize
                    if (Offset >= CFN.FileInfo.FileSize)
                    {
                        logger.Trace($"{CFN.ObjectId} ***Write*** {CFN.Name} [{Path.GetFileName(CFN.Name)}] Case 1");
                        BytesTransferred = default(UInt32);
                        FileInfo = default(FileInfo);
                        DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                        return STATUS_SUCCESS;
                    }

                    EndOffset = Offset + Length;
                    if (EndOffset > CFN.FileInfo.FileSize) EndOffset = CFN.FileInfo.FileSize;
                }
                else
                {
                    if (WriteToEndOfFile) Offset = CFN.FileInfo.FileSize; //We write at the end the file so whatever the Offset is, we set it to be equal to the file size

                    EndOffset = Offset + Length;

                    if (EndOffset > CFN.FileInfo.FileSize) //We are not in a ConstrainedIo so we expand the file size if the EndOffset goes beyond the current file size
                    {
                        logger.Trace($"{CFN.ObjectId} Write Increase FileSize {CFN.Name}");
                        Int32 Result = SetFileSizeInternal(CFN, EndOffset, false);
                        if (Result < 0)
                        {
                            logger.Trace($"{CFN.ObjectId} ***Write*** {CFN.Name} [{Path.GetFileName(CFN.Name)}] Case 2");
                            BytesTransferred = default(UInt32);
                            FileInfo = default(FileInfo);
                            DebugEnd(OperationId, CFN, "Resize failed : " + Result.ToString());
                            return Result; //Keep the precise status (e.g. STATUS_DISK_FULL beyond the maximum file size)
                        }
                    }
                }

                BytesTransferred = (UInt32)(EndOffset - Offset);
                try
                {
                    Marshal.Copy(Buffer, CFN.FileData, (int)Offset, (int)BytesTransferred);
                }
                catch (Exception ex)
                {
                    logger.Trace($"{CFN.ObjectId} Write Exception {ex.Message}");
                    BytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR");
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                //The in-memory content now differs from the server: it is uploaded by the next Flush, Cleanup or Close. (The former
                //FlushAtWrite mode uploaded the whole file after every write: Windows writes by blocks of 64 KB, so a 10 MB file was
                //uploaded about 160 times)
                CFN.HasUnflushedData = true;
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Write", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                RepositoryActionPerformed?.Invoke(this, L);
                FileInfo = CFN.FileInfo;

                if (logger.IsTraceEnabled) logger.Trace($"{CFN.ObjectId} Write {CFN.RepositoryPath} at {Offset} for {BytesTransferred} bytes | Requested {Length} bytes | {ConstrainedIo}");
                DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                return STATUS_SUCCESS;

                /*
                FileNode FileNode = (FileNode)FileNode0;
                UInt64 EndOffset;

                if (ConstrainedIo)
                {
                    if (Offset >= FileNode.FileInfo.FileSize)
                    {
                        BytesTransferred = default(UInt32);
                        FileInfo = default(FileInfo);
                        return STATUS_SUCCESS;
                    }
                    EndOffset = Offset + Length;
                    if (EndOffset > FileNode.FileInfo.FileSize)
                        EndOffset = FileNode.FileInfo.FileSize;
                }
                else
                {
                    if (WriteToEndOfFile)
                        Offset = FileNode.FileInfo.FileSize;
                    EndOffset = Offset + Length;
                    if (EndOffset > FileNode.FileInfo.FileSize)
                    {
                        Int32 Result = SetFileSizeInternal(FileNode, EndOffset, false);
                        if (0 > Result)
                        {
                            BytesTransferred = default(UInt32);
                            FileInfo = default(FileInfo);
                            return Result;
                        }
                    }
                }

                BytesTransferred = (UInt32)(EndOffset - Offset);
                Marshal.Copy(Buffer, FileNode.FileData, (int)Offset, (int)BytesTransferred);

                FileInfo = FileNode.GetFileInfo();
                */
            }
        }

        /// <summary>
        /// WinFSP callback that renames or moves a file or directory on the server (WebDAV MOVE). If the target exists and
        /// <paramref name="ReplaceIfExists"/> is set, the target is deleted first, but only for files in <see cref="WebDAVMode.AOS"/> mode;
        /// otherwise a collision is returned. On success the node's paths are updated and the cache keys are renamed (including the
        /// sub-elements of a renamed folder).
        /// </summary>
        /// <param name="FileName">Current local path.</param>
        /// <param name="NewFileName">New local path.</param>
        /// <param name="ReplaceIfExists">Whether an existing target may be replaced.</param>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_OBJECT_NAME_COLLISION</c>; <c>STATUS_ACCESS_DENIED</c> if the move is refused or fails;
        /// <c>STATUS_CANNOT_MAKE</c> if the target existence check fails; <c>STATUS_NETWORK_UNREACHABLE</c> on transport errors or 401
        /// (which also raises <see cref="RepositoryAuthenticationFailed"/>).</returns>
        public override Int32 Rename(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            String NewFileName,
            Boolean ReplaceIfExists)
        {
            FileNode CFN = (FileNode)FileNode0;
            LogListItem L;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                if (FileName == NewFileName) return STATUS_SUCCESS;

                String RepositoryDocumentName = FileNode.ConvertLocalPathToRepositoryPath(FileName);
                String RepositoryTargetDocumentName = FileNode.ConvertLocalPathToRepositoryPath(NewFileName);

                var Proxy = Client;
                bool IsDirectory = (CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0;

                //A case-only rename (a.txt -> A.txt) targets the element itself on a case-insensitive server: the target must not
                //be looked up, otherwise it is found and reported as a collision, or even replaced with ReplaceIfExists (data loss)
                bool IsCaseOnlyRename = String.Equals(FileName, NewFileName, StringComparison.OrdinalIgnoreCase);

                //A file created on the drive but not uploaded yet (see Create) does not exist on the server: upload it first, so
                //that it can be moved like any other file
                if (CFN.IsNewOnServer && !UploadPendingChanges(CFN, "Rename Flush", OperationId))
                {
                    DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR");
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                Int32 MoveStatus = MoveOnServer(CFN, Proxy, FileName, NewFileName, RepositoryDocumentName, RepositoryTargetDocumentName, IsDirectory, IsCaseOnlyRename, ReplaceIfExists);
                if (MoveStatus != STATUS_SUCCESS)
                {
                    DebugEnd(OperationId, CFN, MoveStatus.ToString());
                    return MoveStatus;
                }

                CFN.RepositoryPath = RepositoryTargetDocumentName;
                CFN.Name = FileNode.GetRepositoryDocumentName(RepositoryTargetDocumentName);
                CFN.LocalPath = NewFileName;
                Cache.RenameFileNodeKey(FileName, NewFileName);
                if (!IsCaseOnlyRename) AddMissingFile(FileName); //The old name no longer exists: later probes of it need no request

                if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
                {
                    Cache.RenameFolderSubElements(FileName, NewFileName);
                }

                /*
                FileNode FileNode = (FileNode)FileNode0;
                FileNode NewFileNode;

                NewFileNode = FileNodeMap.Get(NewFileName);
                if (null != NewFileNode && FileNode != NewFileNode)
                {
                    if (!ReplaceIfExists)
                        return STATUS_OBJECT_NAME_COLLISION;
                    if (0 != (NewFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                        return STATUS_ACCESS_DENIED;
                }

                if (null != NewFileNode && FileNode != NewFileNode)
                    FileNodeMap.Remove(NewFileNode);

                List<String> DescendantFileNames = new List<String>(FileNodeMap.GetDescendantFileNames(FileNode));
                foreach (String DescendantFileName in DescendantFileNames)
                {
                    FileNode DescendantFileNode = FileNodeMap.Get(DescendantFileName);
                    if (null == DescendantFileNode)
                        continue; // should not happen
                    FileNodeMap.Remove(DescendantFileNode);
                    DescendantFileNode.FileName =
                        NewFileName + DescendantFileNode.FileName.Substring(FileName.Length);
                    FileNodeMap.Insert(DescendantFileNode);
                }
                */
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_SUCCESS" };
                DebugEnd(OperationId, null, "STATUS_SUCCESS");
                RepositoryActionPerformed?.Invoke(this, L);
                return STATUS_SUCCESS;
            }
        }

        /// <summary>
        /// Moves an element on the server for <see cref="Rename"/>, with the NTFS rules:
        /// <list type="bullet">
        /// <item>an existing target is a collision unless <paramref name="ReplaceIfExists"/> is set;</item>
        /// <item>with <paramref name="ReplaceIfExists"/>, an existing file is replaced atomically by a single MOVE with "Overwrite: T";</item>
        /// <item>a directory is never replaced, and a directory never replaces a file (STATUS_ACCESS_DENIED, like NTFS): on a WebDAV
        /// server, replacing a collection would delete its whole content;</item>
        /// <item>otherwise the MOVE is sent with "Overwrite: F": if the target exists after all (created meanwhile by another client,
        /// stale cache), the server answers 412 instead of replacing it silently, and the target is replaced only if requested.</item>
        /// </list>
        /// Failures are reported through <see cref="RepositoryActionPerformed"/>.
        /// </summary>
        /// <returns><c>STATUS_SUCCESS</c>; <c>STATUS_OBJECT_NAME_COLLISION</c>; <c>STATUS_ACCESS_DENIED</c> (directory replacement, or the
        /// server refused the MOVE); <c>STATUS_CANNOT_MAKE</c> if the target cannot be checked; <c>STATUS_NETWORK_UNREACHABLE</c> on
        /// transport errors or 401 (which also raises <see cref="RepositoryAuthenticationFailed"/>).</returns>
        private Int32 MoveOnServer(FileNode CFN, WebDavClient Proxy, String FileName, String NewFileName, String Source, String Target, bool IsDirectory, bool IsCaseOnlyRename, bool ReplaceIfExists)
        {
            String LoggedFile = $"{FileName} -> {NewFileName}";
            Int32 Report(Int32 Status, String Result)
            {
                RepositoryActionPerformed?.Invoke(this, new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = LoggedFile, Result = Result });
                return Status;
            }

            bool Move(bool? Overwrite)
            {
                Proxy.Move(Source, Target, IsDirectory, Overwrite).GetAwaiter().GetResult(); //Throws on failure
                return true;
            }

            //1. Is the target free? The cache answers when it knows the target is missing (Office probes its temporary names just
            //before renaming): WinFSP blocks the whole volume during a rename, so every request avoided here unfreezes Explorer sooner
            bool Replace = false;
            try
            {
                if (!IsCaseOnlyRename && ElementExists(NewFileName, Proxy, true))
                {
                    if (!ReplaceIfExists) return Report(STATUS_OBJECT_NAME_COLLISION, "STATUS_OBJECT_NAME_COLLISION");
                    if (IsDirectory || TargetIsDirectory(NewFileName, Proxy)) return Report(STATUS_ACCESS_DENIED, "STATUS_ACCESS_DENIED - A directory cannot be replaced");
                    Replace = true;
                }
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                OnAuthenticationFailed();
                return Report(STATUS_NETWORK_UNREACHABLE, "STATUS_NETWORK_UNREACHABLE");
            }
            catch (HttpRequestException)
            {
                return Report(STATUS_NETWORK_UNREACHABLE, "STATUS_NETWORK_UNREACHABLE");
            }
            catch (Exception)
            {
                return Report(STATUS_CANNOT_MAKE, "STATUS_CANNOT_MAKE");
            }

            //2. Move. A case-only rename is sent without Overwrite header: on a case-insensitive server the "target" is the element
            //itself, which "Overwrite: F" could refuse
            try
            {
                bool Moved;
                try
                {
                    Moved = Move(IsCaseOnlyRename ? (bool?)null : Replace);
                }
                catch (WebDavException ex) when (ex.StatusCode == 412 && ReplaceIfExists && !Replace && !IsDirectory)
                {
                    //The target exists after all: replace it as requested, unless it is a directory
                    if (TargetIsDirectory(NewFileName, Proxy)) return Report(STATUS_ACCESS_DENIED, "STATUS_ACCESS_DENIED - A directory cannot be replaced");
                    Replace = true;
                    Moved = Move(true);
                }

                if (!Moved) return Report(STATUS_ACCESS_DENIED, "STATUS_ACCESS_DENIED");
            }
            catch (WebDavException ex) when (ex.StatusCode == 412)
            {
                //"Overwrite: F" and the target exists on the server after all
                return Report(STATUS_OBJECT_NAME_COLLISION, "STATUS_OBJECT_NAME_COLLISION");
            }
            catch (WebDavException ex) when (ex.StatusCode == 401)
            {
                OnAuthenticationFailed();
                return Report(STATUS_NETWORK_UNREACHABLE, "STATUS_NETWORK_UNREACHABLE");
            }
            catch (HttpRequestException)
            {
                return Report(STATUS_NETWORK_UNREACHABLE, "STATUS_NETWORK_UNREACHABLE");
            }
            catch (Exception)
            {
                return Report(STATUS_ACCESS_DENIED, "STATUS_ACCESS_DENIED");
            }

            //The replaced element no longer exists: forget its node (the renamed node takes its path)
            if (Replace) Cache.DeleteFileEntry(NewFileName);
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// Returns whether the element at a local path is a directory, from the cache or else from the server.
        /// </summary>
        private bool TargetIsDirectory(String FileName, WebDavClient Proxy)
        {
            Cache.Lock();
            try
            {
                FileNode Known = Cache.GetFileNodeNoLock(FileName).node;
                if (Known != null) return (Known.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0;
            }
            finally
            {
                Cache.Unlock();
            }

            WebDavItem Element = GetRepositoryElement(FileName);
            return Element != null && Element.IsCollection;
        }

        /// <summary>
        /// WinFSP callback for FlushFileBuffers (e.g. an application saving a document and asking for the data to be persisted).
        /// Uploads the pending changes of the file, whatever the flush mode (see <see cref="UploadPendingChanges"/>).
        /// A Flush coming less than <see cref="FlushMinimumIntervalInMilliseconds"/> after the last upload is deferred: applications
        /// call it several times in a row while saving, and the changes are uploaded by the next Flush, Cleanup or Close anyway.
        /// A volume flush (<paramref name="FileNode0"/> is null) does nothing.
        /// </summary>
        /// <returns><c>STATUS_SUCCESS</c> if there was nothing to upload or the upload succeeded, <c>STATUS_UNEXPECTED_IO_ERROR</c> otherwise.</returns>
        public override Int32 Flush(
            Object FileNode0,
            Object FileDesc,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;
            if (CFN == null)
            {
                //Flush of the whole volume: pending changes of each file are uploaded by its own Flush/Cleanup/Close
                FileInfo = default(FileInfo);
                return STATUS_SUCCESS;
            }

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                //Applications (Office...) call FlushFileBuffers several times in a row while saving: each call would upload the whole
                //file again. A Flush right after an upload is skipped; the changes are uploaded by the next Flush, Cleanup or Close
                if (CFN.HasUnflushedData && (DateTime.Now - CFN.LastUploadAt).TotalMilliseconds < FlushMinimumIntervalInMilliseconds)
                {
                    FileInfo = CFN.FileInfo;
                    DebugEnd(OperationId, CFN, "STATUS_SUCCESS - Upload deferred (previous upload too recent)");
                    return STATUS_SUCCESS;
                }

                bool Success = UploadPendingChanges(CFN, "Flush", OperationId);
                FileInfo = CFN.FileInfo;

                DebugEnd(OperationId, CFN, Success ? "STATUS_SUCCESS" : "STATUS_UNEXPECTED_IO_ERROR");
                return Success ? STATUS_SUCCESS : STATUS_UNEXPECTED_IO_ERROR;
            }
        }

        /// <summary>
        /// Uploads the in-memory content of a node (up to <c>FileSize</c>) if it has unflushed data and is not deleted.
        /// Must be called with the node's <c>OperationLock</c> held. On success the unflushed flag is cleared. On failure
        /// (refused upload, network error, 401...) a local recovery copy is generated, a 401 is reported through
        /// <see cref="OnAuthenticationFailed"/>, and the flag is kept so that a later Flush/Cleanup/Close can try again.
        /// The result is reported through <see cref="RepositoryActionPerformed"/>.
        /// </summary>
        /// <param name="CFN">Node to upload.</param>
        /// <param name="Method">Name of the operation, used in the activity log (e.g. "Cleanup Flush").</param>
        /// <param name="OperationId">Identifier of the calling operation, used in the debug log.</param>
        /// <returns><c>true</c> if there was nothing to upload or the upload succeeded, <c>false</c> otherwise.</returns>
        private bool UploadPendingChanges(FileNode CFN, String Method, String OperationId)
        {
            if (!CFN.HasUnflushedData || CFN.IsDeleted) return true;

            LogListItem L;
            try
            {
                Stream Content;
                if (CFN.FileData != null) Content = new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize);
                else if (CFN.FileInfo.FileSize == 0) Content = new MemoryStream(new byte[0]); //Emptied file (e.g. overwritten or truncated to 0)
                else throw new InvalidOperationException("The content to upload is not loaded"); //Never upload an empty content in place of the real one

                var Proxy = Client;
                //First upload of a file created on the drive: never replace a file created meanwhile by another client (412 instead)
                Proxy.Put(CFN.RepositoryPath, Content, CFN.IsNewOnServer).GetAwaiter().GetResult(); //Throws on failure

                CFN.HasUnflushedData = false;
                CFN.DeleteLocalCopy(); //The server has the content: a recovery copy from a previous failure is obsolete
                CFN.IsNewOnServer = false;
                CFN.MarkContentInSyncWithServer();
                CFN.LastUploadAt = DateTime.Now;
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = Method, File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, null, $"{Method} : STATUS_SUCCESS");
                return true;
            }
            catch (Exception ex)
            {
                //Whatever the failure, keep a local copy so the user can recover the data
                if (CFN.FileData != null) CFN.GenerateLocalCopy();
                if (IsAuthenticationError(ex)) OnAuthenticationFailed();
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = Method, File = CFN.LocalPath, Result = "STATUS_FAILED", LocalTemporaryPath = CFN.TemporaryLocalCopyPath };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, null, $"{Method} : STATUS_FAILED - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// WinFSP callback that sets attributes and timestamps. Values are only applied to the cached node (a value of -1 for attributes
        /// or 0 for a timestamp means "unchanged"); they are not sent to the server.
        /// </summary>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 SetBasicInfo(
            Object FileNode0,
            Object FileDesc,
            UInt32 FileAttributes,
            UInt64 CreationTime,
            UInt64 LastAccessTime,
            UInt64 LastWriteTime,
            UInt64 ChangeTime,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {

                if (unchecked((UInt32)(-1)) != FileAttributes)
                    CFN.FileInfo.FileAttributes = FileAttributes;
                if (0 != CreationTime)
                    CFN.FileInfo.CreationTime = CreationTime;
                if (0 != LastAccessTime)
                    CFN.FileInfo.LastAccessTime = LastAccessTime;
                if (0 != LastWriteTime)
                    CFN.FileInfo.LastWriteTime = LastWriteTime;
                if (0 != ChangeTime)
                    CFN.FileInfo.ChangeTime = ChangeTime;

                FileInfo = CFN.FileInfo;

                //TODO : We cannot override those informations in Alfresco, so ... what ?
                /*
                FileNode FileNode = (FileNode)FileNode0;

                if (null != FileNode.MainFileNode)
                    FileNode = FileNode.MainFileNode;

                if (unchecked((UInt32)(-1)) != FileAttributes)
                    FileNode.FileInfo.FileAttributes = FileAttributes;
                if (0 != CreationTime)
                    FileNode.FileInfo.CreationTime = CreationTime;
                if (0 != LastAccessTime)
                    FileNode.FileInfo.LastAccessTime = LastAccessTime;
                if (0 != LastWriteTime)
                    FileNode.FileInfo.LastWriteTime = LastWriteTime;
                if (0 != ChangeTime)
                    FileNode.FileInfo.ChangeTime = ChangeTime;

                FileInfo = FileNode.GetFileInfo();
                */
                //Console.WriteLine($"{CFN.handle} SetBasicInfo {CFN.FullPath} in {(DateTime.Now - StartTime).TotalSeconds}");
                return STATUS_SUCCESS;
            }
        }

        /// <summary>
        /// WinFSP callback that changes the file size or the allocation size of the in-memory content through
        /// <see cref="SetFileSizeInternal"/> (which downloads the content first if it is not loaded). Nothing is sent to the server here:
        /// if the file size changed, the node is marked as having unflushed data, uploaded at <see cref="Cleanup"/>/<see cref="Close"/>.
        /// </summary>
        /// <param name="NewSize">New file size or allocation size.</param>
        /// <param name="SetAllocationSize"><c>true</c> to change the allocation size, <c>false</c> to change the file size.</param>
        /// <param name="FileInfo">Receives the updated file information, or a default value if resizing failed.</param>
        /// <returns>The result of <see cref="SetFileSizeInternal"/>.</returns>
        public override Int32 SetFileSize(
            Object FileNode0,
            Object FileDesc,
            UInt64 NewSize,
            Boolean SetAllocationSize,
            out FileInfo FileInfo)
        {
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {

                FileInfo = CFN.FileInfo;

                UInt64 PreviousFileSize = CFN.FileInfo.FileSize;
                Int32 Result = SetFileSizeInternal(CFN, NewSize, SetAllocationSize);
                FileInfo = Result >= 0 ? CFN.FileInfo : default(FileInfo);

                //A change of the file size (truncation or extension) must be uploaded, even if no write follows.
                //A change of the allocation size alone does not change the content.
                if (Result >= 0 && CFN.FileInfo.FileSize != PreviousFileSize) CFN.HasUnflushedData = true;

                logger.Trace($"{CFN.ObjectId} SetFileSize File {CFN.LocalPath}. New Size {NewSize}. Allocation size {SetAllocationSize}. Result {Result}");

                /*
                FileNode FileNode = (FileNode)FileNode0;
                Int32 Result;

                Result = SetFileSizeInternal(FileNode, NewSize, SetAllocationSize);
                FileInfo = 0 <= Result ? FileNode.GetFileInfo() : default(FileInfo);
                */

                return Result;
            }
        }

        /// <summary>
        /// Builds the <see cref="FileNode"/> of an element that has just been created on the server by <see cref="Create"/>.
        /// The node is built locally, without reading the element back from the server: the server has already confirmed the
        /// creation, and WinFSP holds a lock on the parent folder during Create (Explorer cannot list it meanwhile), so the extra
        /// request would only lengthen that freeze. Reading it back could also fail (server not yet consistent...) for an element
        /// that does exist. The server metadata (dates, etag) is picked up by the next refresh of the folder.
        /// </summary>
        /// <param name="Proxy">WebDAV client used for the creation (not used any more, kept for the call sites).</param>
        /// <param name="FileName">Local path of the created element.</param>
        /// <param name="IsDirectory">True for a folder, false for a file.</param>
        private FileNode GetCreatedFileNode(WebDavClient Proxy, String FileName, bool IsDirectory)
        {
            DateTime Now = DateTime.Now;
            var LocalElement = new WebDavItem()
            {
                Path = FileNode.ConvertLocalPathToRepositoryPath(FileName),
                DisplayName = Path.GetFileName(FileName),
                IsCollection = IsDirectory,
                ContentLength = IsDirectory ? (long?)null : 0,
                CreationDate = Now,
                LastModified = Now
            };

            var Node = new FileNode(LocalElement);
            Node.LocalPath = FileName; //The local path is already known (keeps the exact case asked by the application)
            return Node;
        }

        /// <summary>
        /// Makes sure the content of a file node is held in memory (<c>FileData</c>), downloading it from the server if needed.
        /// Must be called with the node's <c>OperationLock</c> held. Nothing is downloaded if the content is already loaded,
        /// or if the node is known to be empty (e.g. a file just created or overwritten) and <paramref name="DownloadIfEmpty"/> is false.
        /// After a download, the file size and allocation size are set to the size of the downloaded content.
        /// </summary>
        /// <param name="DownloadIfEmpty">Download even if the node size is 0. Used for reads, because some WebDAV servers do not
        /// return the content length, in which case a size of 0 does not mean that the file is empty.</param>
        /// <returns><c>STATUS_SUCCESS</c>, or <c>STATUS_NETWORK_UNREACHABLE</c> if the download failed (including on 401,
        /// which also raises <see cref="RepositoryAuthenticationFailed"/>).</returns>
        private Int32 EnsureContentLoaded(FileNode CFN, bool DownloadIfEmpty = false)
        {
            if (CFN.FileData != null) return STATUS_SUCCESS;
            if ((CFN.FileInfo.FileAttributes & (UInt32)System.IO.FileAttributes.Directory) != 0) return STATUS_SUCCESS;
            if (!DownloadIfEmpty && CFN.FileInfo.FileSize == 0 && CFN.FileInfo.AllocationSize == 0) return STATUS_SUCCESS; //Empty file: nothing to download

            Byte[] Content;
            try
            {
                var Proxy = Client;
                Content = Proxy.Get(CFN.RepositoryPath).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (IsAuthenticationError(ex)) OnAuthenticationFailed();
                logger.Trace($"{CFN.ObjectId} Download of {CFN.LocalPath} failed : {ex.Message}");
                return STATUS_NETWORK_UNREACHABLE;
            }

            if (Content == null) return STATUS_NETWORK_UNREACHABLE;

            CFN.FileData = Content;
            CFN.FileInfo.FileSize = (UInt64)Content.LongLength;
            CFN.FileInfo.AllocationSize = (UInt64)Content.LongLength;
            CFN.MarkContentInSyncWithServer();
            return STATUS_SUCCESS;
        }

        /// <summary>
        /// Resizes the in-memory content of a node, loading it from the server first if needed (see <see cref="EnsureContentLoaded"/>).
        /// When <paramref name="SetAllocationSize"/> is <c>true</c>, reallocates <c>FileData</c>
        /// to <paramref name="NewSize"/> bytes (keeping existing data, truncating <c>FileSize</c> if needed). Otherwise changes
        /// <c>FileSize</c>, growing the allocation to the next allocation unit if necessary and zero-filling the extended range.
        /// </summary>
        /// <param name="FileNode">Node to resize.</param>
        /// <param name="NewSize">New allocation size or file size.</param>
        /// <param name="SetAllocationSize"><c>true</c> to set the allocation size, <c>false</c> to set the file size.</param>
        /// <returns><c>STATUS_SUCCESS</c>, <c>STATUS_DISK_FULL</c> if the size exceeds the maximum file size,
        /// <c>STATUS_INSUFFICIENT_RESOURCES</c> if the buffer cannot be allocated, <c>STATUS_NETWORK_UNREACHABLE</c> if the content
        /// had to be downloaded and the download failed, or <c>STATUS_UNEXPECTED_IO_ERROR</c> on any other error (logged).
        /// On error the file size and content are left unchanged.</returns>
        private Int32 SetFileSizeInternal(
            FileNode FileNode,
            UInt64 NewSize,
            Boolean SetAllocationSize)
        {
            //Resizing keeps the existing content: it must be loaded first
            Int32 LoadResult = EnsureContentLoaded(FileNode);
            if (LoadResult < 0) return LoadResult;

            try
            {
                if (SetAllocationSize)
                {
                    if (FileNode.FileInfo.AllocationSize != NewSize)
                    {
                        if (NewSize > MaxFileSize) return STATUS_DISK_FULL;

                        //An empty array, not null: null means "content not loaded yet" and would make the next read download the old content again
                        byte[] FileData = new byte[0];
                        if (NewSize != 0)
                        {
                            try
                            {
                                FileData = new byte[NewSize];
                            }
                            catch
                            {
                                return STATUS_INSUFFICIENT_RESOURCES;
                            }
                        }
                        int CopyLength = (int)Math.Min(FileNode.FileInfo.AllocationSize, NewSize);
                        if (CopyLength != 0) Array.Copy(FileNode.FileData, FileData, CopyLength);

                        FileNode.FileData = FileData;
                        FileNode.FileInfo.AllocationSize = NewSize;
                        if (FileNode.FileInfo.FileSize > NewSize) FileNode.FileInfo.FileSize = NewSize;
                    }
                }
                else
                {
                    if (FileNode.FileInfo.FileSize != NewSize)
                    {
                        if (FileNode.FileInfo.AllocationSize < NewSize)
                        {
                            UInt64 AllocationUnit = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;
                            UInt64 AllocationSize = (NewSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
                            Int32 Result = SetFileSizeInternal(FileNode, AllocationSize, true);
                            if (Result < 0) return Result;
                        }

                        if (NewSize > FileNode.FileInfo.FileSize)
                        {
                            int CopyLength = (int)(NewSize - FileNode.FileInfo.FileSize);
                            if (CopyLength != 0) Array.Clear(FileNode.FileData, (int)FileNode.FileInfo.FileSize, CopyLength);
                        }

                        FileNode.FileInfo.FileSize = NewSize;
                    }
                }
            }
            catch (Exception ex)
            {
                //Report the failure instead of pretending the file was resized: callers would otherwise write into
                //or upload a buffer that does not have the expected size
                logger.Error($"{FileNode.ObjectId} SetFileSizeInternal {ex.Message} {ex.StackTrace}");
                return (ex is OutOfMemoryException || ex is OverflowException) ? STATUS_INSUFFICIENT_RESOURCES : STATUS_UNEXPECTED_IO_ERROR;
            }

            return STATUS_SUCCESS;
        }

        /// <summary>
        /// WinFSP callback to change a security descriptor. Not supported.
        /// </summary>
        /// <returns>Always <c>STATUS_INVALID_DEVICE_REQUEST</c>.</returns>
        public override Int32 SetSecurity(
            Object FileNode0,
            Object FileDesc,
            AccessControlSections Sections,
            Byte[] SecurityDescriptor)
        {
            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            FileNode.FileSecurity = ModifySecurityDescriptor(
                FileNode.FileSecurity, Sections, SecurityDescriptor);
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        /// <summary>
        /// WinFSP callback enumerating named streams. Not implemented (named streams are disabled in <see cref="Init"/>).
        /// </summary>
        /// <returns>Always <c>false</c> (no stream entries).</returns>
        public override Boolean GetStreamEntry(
            Object FileNode0,
            Object FileDesc,
            ref Object Context,
            out String StreamName,
            out UInt64 StreamSize,
            out UInt64 StreamAllocationSize)
        {
            logger.Trace("Not implemented : GetStreamEntry");

            /*
            FileNode FileNode = (FileNode)FileNode0;
            IEnumerator<String> Enumerator = (IEnumerator<String>)Context;

            if (null == Enumerator)
            {
                if (null != FileNode.MainFileNode)
                    FileNode = FileNode.MainFileNode;

                List<String> StreamFileNames = new List<String>();
                if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    StreamFileNames.Add(FileNode.FileName);
                StreamFileNames.AddRange(FileNodeMap.GetStreamFileNames(FileNode));
                Context = Enumerator = StreamFileNames.GetEnumerator();
            }

            while (Enumerator.MoveNext())
            {
                String FullFileName = Enumerator.Current;
                FileNode StreamFileNode = FileNodeMap.Get(FullFileName);
                if (null != StreamFileNode)
                {
                    int Index = FullFileName.IndexOf(':');
                    if (0 > Index)
                        StreamName = "";
                    else
                        StreamName = FullFileName.Substring(Index + 1);
                    StreamSize = StreamFileNode.FileInfo.FileSize;
                    StreamAllocationSize = StreamFileNode.FileInfo.AllocationSize;
                    return true;
                }
            }
            */
            StreamName = default(String);
            StreamSize = default(UInt64);
            StreamAllocationSize = default(UInt64);
            return false;
        }

        #region Reparse points

        /// <summary>
        /// WinFSP callback to get a reparse point by file name. Not implemented (reparse points are disabled in <see cref="Init"/>).
        /// </summary>
        /// <returns>Always <c>STATUS_INVALID_DEVICE_REQUEST</c>.</returns>
        public override Int32 GetReparsePointByName(
            String FileName,
            Boolean IsDirectory,
            ref Byte[] ReparseData)
        {
            logger.Trace("Not implemented : GetReparsePointByName");

            /*
            FileNode FileNode;

            FileNode = FileNodeMap.Get(FileName);
            if (null == FileNode)
                return STATUS_OBJECT_NAME_NOT_FOUND;

            if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            ReparseData = FileNode.ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        /// <summary>
        /// WinFSP callback to get the reparse point of an open file. Not implemented.
        /// </summary>
        /// <returns>Always <c>STATUS_INVALID_DEVICE_REQUEST</c>.</returns>
        public override Int32 GetReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            ref Byte[] ReparseData)
        {
            logger.Trace("Not implemented : GetReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (0 == (FileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            ReparseData = FileNode.ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        /// <summary>
        /// WinFSP callback to set a reparse point. Not implemented.
        /// </summary>
        /// <returns>Always <c>STATUS_INVALID_DEVICE_REQUEST</c>.</returns>
        public override Int32 SetReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            Byte[] ReparseData)
        {
            logger.Trace("Not implemented : SetReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (FileNodeMap.HasChild(FileNode))
                return STATUS_DIRECTORY_NOT_EMPTY;

            if (null != FileNode.ReparseData)
            {
                Int32 Result = CanReplaceReparsePoint(FileNode.ReparseData, ReparseData);
                if (0 > Result)
                    return Result;
            }

            FileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.ReparsePoint;
            FileNode.FileInfo.ReparseTag = GetReparseTag(ReparseData);
            FileNode.ReparseData = ReparseData;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        /// <summary>
        /// WinFSP callback to delete a reparse point. Not implemented.
        /// </summary>
        /// <returns>Always <c>STATUS_INVALID_DEVICE_REQUEST</c>.</returns>
        public override Int32 DeleteReparsePoint(
            Object FileNode0,
            Object FileDesc,
            String FileName,
            Byte[] ReparseData)
        {
            logger.Trace("Not implemented : DeleteReparsePoint");

            /*
            FileNode FileNode = (FileNode)FileNode0;

            if (null != FileNode.MainFileNode)
                FileNode = FileNode.MainFileNode;

            if (null != FileNode.ReparseData)
            {
                Int32 Result = CanReplaceReparsePoint(FileNode.ReparseData, ReparseData);
                if (0 > Result)
                    return Result;
            }
            else
                return STATUS_NOT_A_REPARSE_POINT;

            FileNode.FileInfo.FileAttributes &= ~(UInt32)FileAttributes.ReparsePoint;
            FileNode.FileInfo.ReparseTag = 0;
            FileNode.ReparseData = null;
            */
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        #endregion

        /// <summary>
        /// WinFSP callback invoked after the volume is unmounted: clears the metadata cache, then calls the base implementation.
        /// </summary>
        public override void Unmounted(object Host)
        {
            Cache.Clear();
            lock (RetainedContentsLock) RetainedContents.Clear();
            base.Unmounted(Host);
            Http.Dispose(); //The connections of this mount are no longer needed
        }

        /// <summary>
        /// Notifies the application that the server rejected the credentials (HTTP 401). Only the first call per mount
        /// raises <see cref="RepositoryAuthenticationFailed"/>; the application is then expected to unmount the drive.
        /// The cache is left untouched: it is cleared by <see cref="Unmounted"/>.
        /// </summary>
        private void OnAuthenticationFailed()
        {
            if (Interlocked.Exchange(ref AuthenticationFailureNotified, 1) == 0)
            {
                logger.Warn("The server rejected the credentials (HTTP 401)");
                RepositoryAuthenticationFailed?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Returns true if the exception is a WebDAV error caused by rejected credentials (HTTP 401).
        /// </summary>
        private static bool IsAuthenticationError(Exception ex)
        {
            return ex is WebDavException WebDAVEx && WebDAVEx.StatusCode == 401;
        }

        /// <summary>
        /// Logs (Trace level) the start of an operation, with the node serialized as JSON.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="CFN">Node the operation applies to.</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void TraceStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsTraceEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Trace($"{OperationId}|{Caller}|Start|{JsonConvert.SerializeObject(CFN)}");
        }

        /// <summary>
        /// Logs (Trace level) the start of an operation on a file name.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="FileName">Local path the operation applies to.</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void TraceStart(String OperationId, String FileName, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsTraceEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Trace($"{OperationId}|{Caller}|Start|{FileName}");
        }

        /// <summary>
        /// Logs (Trace level) the end of an operation with its result. <paramref name="CFN"/> is not logged.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="CFN">Node the operation applied to (unused).</param>
        /// <param name="Result">Result text (usually the NTSTATUS name).</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void TraceEnd(String OperationId, FileNode CFN, String Result, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsTraceEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Trace($"{OperationId}|{Caller}|End|{Result}");
        }

        /// <summary>
        /// Logs (Debug level) the start of an operation, with the node serialized as JSON.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="CFN">Node the operation applies to.</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void DebugStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsDebugEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Debug($"{OperationId}|{Caller}|Start|{JsonConvert.SerializeObject(CFN)}");
        }

        /// <summary>
        /// Logs (Debug level) the start of an operation on a file name.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="FileName">Local path the operation applies to.</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void DebugStart(String OperationId, String FileName, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsDebugEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Debug($"{OperationId}|{Caller}|Start|{FileName}");
        }

        /// <summary>
        /// Logs (Debug level) the end of an operation with its result. <paramref name="CFN"/> is not logged.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="CFN">Node the operation applied to (unused).</param>
        /// <param name="Result">Result text (usually the NTSTATUS name).</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void DebugEnd(String OperationId, FileNode CFN, String Result, [CallerMemberName]string Caller = "")
        {
            if (!logger.IsDebugEnabled) return; //Do not build the message (JSON serialization of the node) when it is not logged
            logger.Debug($"{OperationId}|{Caller}|End|{Result}");
        }
    }

    /// <summary>
    /// Miscellaneous extension methods.
    /// </summary>
    public static class Extension
    {
        /// <summary>
        /// Moves the value stored under <paramref name="fromKey"/> to <paramref name="toKey"/> in a dictionary.
        /// An existing value under <paramref name="toKey"/> is overwritten.
        /// </summary>
        /// <param name="dic">Dictionary to modify.</param>
        /// <param name="fromKey">Current key.</param>
        /// <param name="toKey">New key.</param>
        /// Does nothing if <paramref name="fromKey"/> is not present.
        public static void RenameKey<TKey, TValue>(this IDictionary<TKey, TValue> dic,
                                      TKey fromKey, TKey toKey)
        {
            //The key may be missing (e.g. a node invalidated after a failed upload, then renamed): nothing to rename then
            if (!dic.TryGetValue(fromKey, out TValue value)) return;
            dic.Remove(fromKey);
            dic[toKey] = value;
        }
    }

    // LogWriter (disabled): accumulated timestamped log lines for a single operation and computed its total duration
    // in Write(); the actual logging call was already commented out.
    /*
    public class LogWriter
    {
        StringBuilder SB = new StringBuilder();

        public DateTime StartDate { get; }

        public String FileName;
        public String Caller;
        public Guid OperationId;
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public LogWriter(Guid operationId, String fileName, [CallerMemberName]string caller = "")
        {
            this.OperationId = operationId;
            this.StartDate = DateTime.Now;
            this.FileName = fileName;
            this.Caller = caller;
            SB.AppendLine($"{DateTime.Now.ToString("HH:mm:ss:fff")} - {caller} -> {fileName}");
        }

        public LogWriter(String fileName, [CallerMemberName]string caller = "")
        {
            this.StartDate = DateTime.Now;
            this.FileName = fileName;
            this.Caller = caller;
            SB.AppendLine($"{DateTime.Now.ToString("HH:mm:ss:fff")} - {caller} -> {fileName}");
        }

        public void Append(String text)
        {
            SB.AppendLine($"{DateTime.Now.ToString("HH:mm:ss:fff")} - {text}");
        }

        public void Write()
        {
            double TotalProcessingTimeInSec = (DateTime.Now - StartDate).TotalMilliseconds;
            SB.AppendLine($"Total millisec : {TotalProcessingTimeInSec}");
            //if (TotalProcessingTimeInSec > 1d)
            //if (Caller == "ReadDirectoryEntry" || Caller == "Open" || Caller == "Read")
            //logger.Trace(SB.ToString());
        }
    }
    */
}