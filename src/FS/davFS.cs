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
using WebDAVClient.Helpers;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace KS2Drive.FS
{
    /// <summary>
    /// WinFSP user-mode file system that exposes a remote WebDAV repository as a Windows drive.
    /// Each WinFSP callback is translated into WebDAV calls through <see cref="WebDavClient2"/>; file metadata
    /// (<see cref="FileNode"/>) is kept in a <see cref="CacheManager"/>, and file contents are held in memory
    /// (<c>FileNode.FileData</c>) while the file has open handles. Modified contents are uploaded either on every
    /// <see cref="Write"/> (<see cref="FlushMode.FlushAtWrite"/>) or at <see cref="Cleanup"/>/<see cref="Close"/>
    /// (<see cref="FlushMode.FlushAtCleanup"/>).
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
        private FlushMode FlushMode;
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
        /// Creates the file system from the user configuration: parses the server URL into server, host and
        /// document library path, initializes <see cref="FileNode"/> and <see cref="WebDavClient2"/> (with an optional
        /// client certificate), creates the metadata cache, then tests the connection by listing the repository root.
        /// </summary>
        /// <param name="config">Mount configuration (server URL and type, credentials, flush and kernel cache modes, etc.).</param>
        /// <exception cref="Exception">Thrown when the test listing fails, with a dedicated message for invalid credentials (HTTP 401).</exception>
        public DavFS(Configuration config)
        {
            WebDAVMode webDAVMode = (WebDAVMode)config.ServerType;
            FlushMode flushMode = (FlushMode)Enum.ToObject(typeof(FlushMode), config.FlushMode);
            KernelCacheMode kernelCacheMode = (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), config.KernelCacheMode);

            this.MaxFileNodes = 500000;
            this.MaxFileSize = 0x7FFFFFC7; //Largest byte array .NET can allocate (file contents are held in a byte[]): bigger files are refused with STATUS_DISK_FULL instead of failing later
            this.FlushMode = flushMode;
            this.WebDAVMode = webDAVMode;
            this.kernelCacheMode = kernelCacheMode;
            this.MountAsNetworkDrive = config.MountAsNetworkDrive;

            var DavServerURI = new Uri(config.ServerURL);
            this.DAVServer = DavServerURI.GetLeftPart(UriPartial.Authority);
            this.DAVServeurAuthority = DavServerURI.DnsSafeHost;
            this.DocumentLibraryPath = DavServerURI.PathAndQuery.EndsWith("/") ? DavServerURI.PathAndQuery.Remove(DavServerURI.PathAndQuery.Length - 1) : DavServerURI.PathAndQuery;

            this.DAVLogin = config.ServerLogin;
            this.DAVPassword = config.ServerPassword;

            FileNode.Init(this.DocumentLibraryPath, this.WebDAVMode);
            WebDavClient2.Init(this.DAVServer, this.DocumentLibraryPath, this.DAVLogin, this.DAVPassword, config.UseClientCertForAuthentication ? Tools.FindCertificate(config.CertStoreName, config.CertStoreLocation, config.CertSerial) : null);
            Cache = new CacheManager(CacheMode.Enabled, config.PreLoading);

            //Test connection to server with the parameters entered in the configuration screen
            var Proxy = new WebDavClient2();
            try
            {
                var LisTest = Proxy.List("/").GetAwaiter().GetResult();
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                throw new Exception($"Cannot connect to server : Invalid credentials");
            }
            catch (Exception ex)
            {
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

            try
            {
                Cache.Lock();

                var KnownNode = Cache.GetFileNodeNoLock(FileName);
                if (KnownNode.node == null)
                {
                    if (KnownNode.IsNonExistent)
                    {
                        //The file is known to be non-existent
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        TraceEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    WebDAVClient.Model.Item FoundElement;

                    try
                    {
                        var Proxy = new WebDavClient2();
                        FoundElement = Proxy.GetRepositoryElement(FileName);
                    }
                    catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                    {
                        OnAuthenticationFailed();
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }
                    catch (WebDAVException ex)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        TraceEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }
                    catch (HttpRequestException ex)
                    {
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        TraceEnd(OperationId, null, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (Exception ex)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        TraceEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                        return FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    if (FoundElement == null)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        FileAttributes = (UInt32)System.IO.FileAttributes.Normal;
                        TraceEnd(OperationId, null, "STATUS_OBJECT_NAME_NOT_FOUND");
                        return FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    var D = Cache.AddFileNodeNoLock(new FileNode(FoundElement)); //Use the cached instance if the path is already known (e.g. with another case)
                    if (SecurityDescriptor != null) SecurityDescriptor = D.FileSecurity;
                    FileAttributes = D.FileInfo.FileAttributes;
                    TraceEnd(OperationId, D, "STATUS_SUCCESS - From Repository");
                    return STATUS_SUCCESS;

                }
                else
                {
                    FileAttributes = KnownNode.node.FileInfo.FileAttributes;
                    if (null != SecurityDescriptor) SecurityDescriptor = KnownNode.node.FileSecurity;
                    TraceEnd(OperationId, KnownNode.node, "STATUS_SUCCESS - From Cache");
                    return STATUS_SUCCESS;
                }
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

            try
            {
                Cache.Lock();

                var KnownNode = Cache.GetFileNodeNoLock(FileName);

                if (KnownNode.node == null)
                {
                    if (KnownNode.IsNonExistent)
                    {
                        //The file is known to be non-existent
                        DebugEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    WebDAVClient.Model.Item RepositoryObject;

                    try
                    {
                        var Proxy = new WebDavClient2();
                        RepositoryObject = Proxy.GetRepositoryElement(FileName);
                    }
                    catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                    {
                        OnAuthenticationFailed();
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }
                    catch (WebDAVException ex)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        DebugEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }
                    catch (HttpRequestException ex)
                    {
                        DebugEnd(OperationId, null, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (Exception ex)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        DebugEnd(OperationId, null, $"STATUS_OBJECT_NAME_NOT_FOUND - {ex.Message}");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    if (RepositoryObject == null)
                    {
                        Cache.AddMissingFileNoLock(FileName);
                        DebugEnd(OperationId, null, "STATUS_OBJECT_NAME_NOT_FOUND");
                        return STATUS_OBJECT_NAME_NOT_FOUND;
                    }

                    KnownNode.node = Cache.AddFileNodeNoLock(new FileNode(RepositoryObject)); //Use the cached instance if the path is already known (e.g. with another case)

                    Int32 i = Interlocked.Increment(ref KnownNode.node.OpenCount);
                    DebugEnd(OperationId, KnownNode.node, $"STATUS_SUCCESS - From Repository - Handle {i}");
                }
                else
                {
                    Int32 i = Interlocked.Increment(ref KnownNode.node.OpenCount);
                    DebugEnd(OperationId, KnownNode.node, $"STATUS_SUCCESS - From cache - Handle {i}");
                }

                FileNode0 = KnownNode.node;
                FileInfo = KnownNode.node.FileInfo;
                NormalizedName = FileName;
            }
            finally
            {
                Cache.Unlock();
            }

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
        /// WinFSP callback asking whether a file or directory may be deleted. Deletion is always allowed here; the actual
        /// deletion on the server happens in <see cref="Cleanup"/> when the delete flag is set.
        /// </summary>
        /// <returns>Always <c>STATUS_SUCCESS</c>.</returns>
        public override Int32 CanDelete(
            Object FileNode0,
            Object FileDesc,
            String FileName)
        {
            return STATUS_SUCCESS;
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
            var Proxy = new WebDavClient2();

            try
            {
                WebDAVClient.Model.Item KnownRepositoryElement = Proxy.GetRepositoryElement(FileName);
                if (KnownRepositoryElement != null)
                {
                    DebugEnd(OperationId, null, "STATUS_OBJECT_NAME_COLLISION");
                    return STATUS_OBJECT_NAME_COLLISION;
                }
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
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

            String NewDocumentName = Path.GetFileName(FileName);
            String NewDocumentParentPath = Path.GetDirectoryName(FileName);
            String RepositoryNewDocumentParentPath = FileNode.ConvertLocalPathToRepositoryPath(NewDocumentParentPath);

            if ((FileAttributes & (UInt32)System.IO.FileAttributes.Directory) == 0)
            {
                try
                {
                    if (Proxy.Upload(RepositoryNewDocumentParentPath, new MemoryStream(new byte[0]), NewDocumentName).GetAwaiter().GetResult())
                    {
                        CFN = GetCreatedFileNode(Proxy, FileName, false);
                        CFN.HasUnflushedData = true;
                    }
                    else
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_CANNOT_MAKE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_CANNOT_MAKE");
                        return STATUS_CANNOT_MAKE;
                    }
                }
                catch (WebDAVConflictException)
                {
                    //Seems that we get Conflict response when the folder cannot be created
                    //As we do conflict checking before running the CreateDir command, we consider it as a permission issue
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_ACCESS_DENIED" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                    return STATUS_ACCESS_DENIED;
                }
                catch (HttpRequestException)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_NETWORK_UNREACHABLE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_NETWORK_UNREACHABLE");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                {
                    OnAuthenticationFailed();
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (WebDAVException)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_CANNOT_MAKE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_CANNOT_MAKE");
                    return STATUS_CANNOT_MAKE;
                }
                catch (Exception)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_ACCESS_DENIED" };
                    logger.Trace(JsonConvert.SerializeObject(L));
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                    return STATUS_ACCESS_DENIED;
                }
            }
            else
            {
                try
                {
                    if (Proxy.CreateDir(RepositoryNewDocumentParentPath, NewDocumentName).GetAwaiter().GetResult())
                    {
                        CFN = GetCreatedFileNode(Proxy, FileName, true);
                    }
                    else
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_CANNOT_MAKE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_CANNOT_MAKE");
                        return STATUS_CANNOT_MAKE;
                    }
                }
                catch (WebDAVConflictException)
                {
                    //Seems that we get Conflict response when the folder cannot be created
                    //As we do conflict checking before running the CreateDir command, we consider it as a permission issue
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = "None", Method = "Create", File = FileName, Result = "STATUS_ACCESS_DENIED" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                    return STATUS_ACCESS_DENIED;
                }
                catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                {
                    OnAuthenticationFailed();
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (WebDAVException)
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
                    CFN.FileData = null;
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
        /// (see <see cref="UploadPendingChanges"/>). The open handle count is then decremented and, when it reaches zero,
        /// the in-memory content is released.
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
                    //No more handle on the file, we free its content. Changes that could not be uploaded are only kept in the
                    //local recovery copy: the flag is cleared so that the node can be refreshed from the server again
                    CFN.FileData = null;
                    CFN.HasUnflushedData = false;
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

                if ((Flags & CleanupDelete) != 0)
                {
                    var Proxy = new WebDavClient2();
                    if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
                    {
                        try
                        {
                            //File
                            Proxy.DeleteFile(CFN.RepositoryPath).GetAwaiter().GetResult();
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
                            Proxy.DeleteFolder(CFN.RepositoryPath).GetAwaiter().GetResult();
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
        /// In <see cref="FlushMode.FlushAtWrite"/> mode the whole content (up to <c>FileSize</c>) is uploaded after each write and the node
        /// is invalidated in the cache on failure; in other modes the node is only marked as having unflushed data, to be uploaded at
        /// <see cref="Cleanup"/>/<see cref="Close"/>. If the content is not loaded yet, it is downloaded first so that the bytes outside
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
                            DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR : " + Result.ToString());
                            return STATUS_UNEXPECTED_IO_ERROR;
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

                //The in-memory content now differs from the server. In FlushAtWrite mode the flag is cleared once the upload succeeds;
                //if it fails, Cleanup/Close will try again and keep a local copy
                CFN.HasUnflushedData = true;

                if (this.FlushMode == FlushMode.FlushAtWrite)
                {
                    var Proxy = new WebDavClient2();
                    try
                    {
                        if (!Proxy.Upload(FileNode.GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize), CFN.Name).GetAwaiter().GetResult())
                        {
                            throw new Exception();
                        }
                    }
                    catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                    {
                        OnAuthenticationFailed();
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_NETWORK_UNREACHABLE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, CFN, "STATUS_NETWORK_UNREACHABLE");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (WebDAVConflictException)
                    {
                        Cache.InvalidateFileNode(CFN);
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_ACCESS_DENIED" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, CFN, "STATUS_ACCESS_DENIED");
                        return STATUS_ACCESS_DENIED;
                    }
                    catch (HttpRequestException)
                    {
                        Cache.InvalidateFileNode(CFN);
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_NETWORK_UNREACHABLE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, CFN, "STATUS_NETWORK_UNREACHABLE");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (Exception)
                    {
                        Cache.InvalidateFileNode(CFN);
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_UNEXPECTED_IO_ERROR" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR");
                        return STATUS_UNEXPECTED_IO_ERROR;
                    }

                    CFN.HasUnflushedData = false; //The whole content has been uploaded, including any previous resize
                    CFN.DeleteLocalCopy(); //The server has the content: a recovery copy from a previous failure is obsolete
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                }
                else
                {
                    CFN.HasUnflushedData = true;
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Write", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                }
                FileInfo = CFN.FileInfo;

                logger.Trace($"{CFN.ObjectId} Write {CFN.RepositoryPath} at {Offset} for {BytesTransferred} bytes | Requested {Length} bytes | {ConstrainedIo}");
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

                var Proxy = new WebDavClient2();

                try
                {
                    WebDAVClient.Model.Item KnownRepositoryElement = Proxy.GetRepositoryElement(NewFileName);
                    if (KnownRepositoryElement != null)
                    {
                        if (!ReplaceIfExists)
                        {
                            DebugEnd(OperationId, CFN, $"STATUS_OBJECT_NAME_COLLISION");
                            return STATUS_OBJECT_NAME_COLLISION;
                        }
                        else
                        {
                            if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0 && WebDAVMode == WebDAVMode.AOS)
                            {
                                //The target must be deleted before the MOVE is sent: wait for the deletion to complete.
                                //A 404 means it is already gone, which is what we want
                                try
                                {
                                    Proxy.DeleteFile(FileNode.ConvertLocalPathToRepositoryPath(NewFileName)).GetAwaiter().GetResult();
                                }
                                catch (WebDAVException ex) when (ex.GetHttpCode() == 404)
                                {
                                }
                                Cache.DeleteFileEntry(NewFileName);
                            }
                            else
                            {
                                DebugEnd(OperationId, CFN, $"STATUS_OBJECT_NAME_COLLISION");
                                return STATUS_OBJECT_NAME_COLLISION;
                            }
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_NETWORK_UNREACHABLE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, CFN, $"STATUS_NETWORK_UNREACHABLE");
                    return STATUS_NETWORK_UNREACHABLE;
                }
                catch (Exception ex)
                {
                    if (IsAuthenticationError(ex)) OnAuthenticationFailed();
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_CANNOT_MAKE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, CFN, $"STATUS_CANNOT_MAKE");
                    return FileSystemBase.STATUS_CANNOT_MAKE;
                }

                if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
                {
                    //File
                    try
                    {
                        if (!Proxy.MoveFile(RepositoryDocumentName, RepositoryTargetDocumentName).GetAwaiter().GetResult())
                        {
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_ACCESS_DENIED" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                            return STATUS_ACCESS_DENIED;
                        }
                    }
                    catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                    {
                        OnAuthenticationFailed();
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (HttpRequestException)
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_NETWORK_UNREACHABLE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_NETWORK_UNREACHABLE");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (Exception)
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_ACCESS_DENIED" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                        return FileSystemBase.STATUS_ACCESS_DENIED;
                    }
                }
                else
                {
                    //Directory
                    try
                    {
                        if (!Proxy.MoveFolder(RepositoryDocumentName, RepositoryTargetDocumentName).GetAwaiter().GetResult())
                        {
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_ACCESS_DENIED" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                            return STATUS_ACCESS_DENIED;
                        }
                    }
                    catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
                    {
                        OnAuthenticationFailed();
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (HttpRequestException)
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_NETWORK_UNREACHABLE" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_NETWORK_UNREACHABLE");
                        return STATUS_NETWORK_UNREACHABLE;
                    }
                    catch (Exception)
                    {
                        L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_ACCESS_DENIED" };
                        RepositoryActionPerformed?.Invoke(this, L);
                        DebugEnd(OperationId, null, "STATUS_ACCESS_DENIED");
                        return FileSystemBase.STATUS_ACCESS_DENIED;
                    }
                }

                CFN.RepositoryPath = RepositoryTargetDocumentName;
                CFN.Name = FileNode.GetRepositoryDocumentName(RepositoryTargetDocumentName);
                CFN.LocalPath = NewFileName;
                Cache.RenameFileNodeKey(FileName, NewFileName);

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
        /// WinFSP callback for FlushFileBuffers (e.g. an application saving a document and asking for the data to be persisted).
        /// Uploads the pending changes of the file, whatever the flush mode (see <see cref="UploadPendingChanges"/>).
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

                var Proxy = new WebDavClient2();
                if (!Proxy.Upload(FileNode.GetRepositoryParentPath(CFN.RepositoryPath), Content, CFN.Name).GetAwaiter().GetResult())
                {
                    throw new Exception("Upload failed");
                }

                CFN.HasUnflushedData = false;
                CFN.DeleteLocalCopy(); //The server has the content: a recovery copy from a previous failure is obsolete
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
        /// The element is read back from the server; if it cannot be found (server not yet consistent, folder name containing
        /// a dot...) the node is built locally, since the server has already confirmed the creation. This avoids reporting a
        /// failure for an element that exists on the server, which would then make every retry fail with a name collision.
        /// </summary>
        /// <param name="Proxy">WebDAV client used for the creation.</param>
        /// <param name="FileName">Local path of the created element.</param>
        /// <param name="IsDirectory">True for a folder, false for a file.</param>
        /// <exception cref="WebDAVException">HTTP 401 while reading the element back (handled by the caller).</exception>
        private FileNode GetCreatedFileNode(WebDavClient2 Proxy, String FileName, bool IsDirectory)
        {
            try
            {
                WebDAVClient.Model.Item CreatedElement = Proxy.GetRepositoryElement(FileName);
                if (CreatedElement != null) return new FileNode(CreatedElement);
                logger.Trace($"Create {FileName} : created element not found on the server, building it locally");
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Trace($"Create {FileName} : cannot read the created element back ({ex.Message}), building it locally");
            }

            DateTime Now = DateTime.Now;
            var LocalElement = new WebDAVClient.Model.Item()
            {
                Href = FileNode.ConvertLocalPathToRepositoryPath(FileName),
                DisplayName = Path.GetFileName(FileName),
                IsCollection = IsDirectory,
                ContentLength = IsDirectory ? (long?)null : 0,
                CreationDate = Now,
                LastModified = Now
            };

            var Node = new FileNode(LocalElement);
            Node.LocalPath = FileName; //Do not rely on the URL decoding of the constructor: the local path is already known
            Node.FileInfo.FileAttributes = IsDirectory ? (UInt32)FileAttributes.Directory : (UInt32)FileAttributes.Normal; //Do not rely on the WebDAV directory detection heuristic
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
                var Proxy = new WebDavClient2();
                Content = Proxy.Download(CFN.RepositoryPath).GetAwaiter().GetResult();
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

                        byte[] FileData = null;
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
            base.Unmounted(Host);
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
            return ex is WebDAVException WebDAVEx && WebDAVEx.GetHttpCode() == 401;
        }

        /// <summary>
        /// Logs (Trace level) the start of an operation, with the node serialized as JSON.
        /// </summary>
        /// <param name="OperationId">Identifier correlating the start and end log entries.</param>
        /// <param name="CFN">Node the operation applies to.</param>
        /// <param name="Caller">Calling method name (filled in by the compiler).</param>
        private void TraceStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
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
        /// <exception cref="KeyNotFoundException">Thrown if <paramref name="fromKey"/> is not present.</exception>
        public static void RenameKey<TKey, TValue>(this IDictionary<TKey, TValue> dic,
                                      TKey fromKey, TKey toKey)
        {
            TValue value = dic[fromKey];
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