using Fsp;
using KS2Drive.Config;
using KS2Drive.Log;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;
using WebDAVClient.Helpers;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace KS2Drive.FS
{
    public class DavFS : FileSystemBase
    {
        public event EventHandler<LogListItem> RepositoryActionPerformed;
        public event EventHandler RepositoryAuthenticationFailed;
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private FileSystemHost Host;
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
        private WebDavClient2 UploadClient;
        private WebDavClient2 DownloadClient;
        private Task<bool> UploadTask;
        private Task ContinuedTask;
        private Task DownloadTask;

        private const UInt16 MEMFS_SECTOR_SIZE = 4096;
        private const UInt16 MEMFS_SECTORS_PER_ALLOCATION_UNIT = 1;

        public DavFS(Configuration config)
        {
            WebDAVMode webDAVMode = (WebDAVMode)config.ServerType;
            FlushMode flushMode = (FlushMode)Enum.ToObject(typeof(FlushMode), config.FlushMode);
            KernelCacheMode kernelCacheMode = (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), config.KernelCacheMode);

            this.MaxFileNodes = 500000;
            this.MaxFileSize = UInt32.MaxValue;
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
            this.UploadClient = new WebDavClient2(Timeout.InfiniteTimeSpan);

            //Test connection to server with the parameters entered in the configuration screen
            this.DownloadClient = new WebDavClient2();
            try
            {
                var LisTest = this.DownloadClient.List("/").GetAwaiter().GetResult();
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                throw new Exception($"Cannot connect to server : Invalid credentials");
            }
            catch (Exception ex)
            {
                throw new Exception($"Cannot connect to server : {ex.Message}");
            }

            this.UploadTask = null;
            this.ContinuedTask = null;
            this.DownloadTask = null;
        }

        /// <summary>
        /// Define the general parameters of the file system
        /// </summary>
        public override Int32 Init(Object Host0)
        {
            Host = (FileSystemHost)Host0;

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
        /// Information on the volume (sizes, names)
        /// </summary>
        public override Int32 GetVolumeInfo(
            out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default(VolumeInfo);
            VolumeInfo.TotalSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.FreeSize = MaxFileNodes * (UInt64)MaxFileSize;
            VolumeInfo.SetVolumeLabel(VolumeLabel);
            return STATUS_SUCCESS;
        }

        public override Int32 SetVolumeLabel(
            String VolumeLabel,
            out VolumeInfo VolumeInfo)
        {
            this.VolumeLabel = VolumeLabel;
            return GetVolumeInfo(out VolumeInfo);
        }

        /// <summary>
        /// GetSecurityByName is used by WinFsp to retrieve essential metadata about a file to be opened, such as its attributes and security descriptor.
        /// </summary>
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
                        RepositoryAuthenticationFailed?.Invoke(this, null);
                        Cache.Clear();
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

                    var D = new FileNode(FoundElement);
                    if (SecurityDescriptor != null) SecurityDescriptor = D.FileSecurity;
                    FileAttributes = D.FileInfo.FileAttributes;
                    Cache.AddFileNodeNoLock(D);
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
                        RepositoryAuthenticationFailed?.Invoke(this, null);
                        Cache.Clear();
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

                    KnownNode.node = new FileNode(RepositoryObject);
                    Cache.AddFileNodeNoLock(KnownNode.node);

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

        public override Boolean ReadDirectoryEntry(
            Object FileNode0,
            Object FileDesc,
            String Pattern,
            String Marker, //Détermine à partir de quel fichier dans le répertoire, on commence à lire (exclusif) (null = start from the beginning)
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
                        RepositoryAuthenticationFailed?.Invoke(this, null);
                        Cache.Clear();
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


        public override Int32 CanDelete(
            Object FileNode0,
            Object FileDesc,
            String FileName)
        {
            return STATUS_SUCCESS;
        }

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
                RepositoryAuthenticationFailed?.Invoke(this, null);
                Cache.Clear();
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
                    CFN = new FileNode(FileName);
                    UploadTask = CFN.Upload(new WebDavClient2(Timeout.InfiniteTimeSpan), null, 0, 0);
                    ContinuedTask = null;
                    CFN.HasUnflushedData = true;
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
                    RepositoryAuthenticationFailed?.Invoke(this, null);
                    Cache.Clear();
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
                        CFN = new FileNode(Proxy.GetRepositoryElement(FileName));
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
                    RepositoryAuthenticationFailed?.Invoke(this, null);
                    Cache.Clear();
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
            Cache.AddFileNodeNoLock(CFN);
            Cache.Unlock();

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

        public override void Close(
            Object FileNode0,
            Object FileDesc)
        {
            FileNode CFN = (FileNode)FileNode0;
            LogListItem L;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                if (this.FlushMode == FlushMode.FlushAtCleanup)
                {
                    if (CFN.HasUnflushedData && !CFN.IsDeleted)
                    {
                        try
                        {
                            if (CFN.FileData != null)
                            {
                                var Proxy = new WebDavClient2();
                                if (!Proxy.Upload(FileNode.GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize), CFN.Name).GetAwaiter().GetResult())
                                {
                                    CFN.GenerateLocalCopy();
                                    throw new Exception("Upload failed");
                                }
                            }
                            CFN.HasUnflushedData = false;
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Close Flush", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_SUCCESS");
                        }
                        catch (Exception)
                        {
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Close Flush", File = CFN.LocalPath, Result = "STATUS_FAILED", LocalTemporaryPath = CFN.TemporaryLocalCopyPath };
                            RepositoryActionPerformed?.Invoke(this, L);
                            //Cache.DeleteFileNode(CFN); //TODO : ??
                            DebugEnd(OperationId, null, "STATUS_FAILED");
                            return;
                        }
                    }
                }

                CFN.FlushUpload();

                Int32 HandleCount = Interlocked.Decrement(ref CFN.OpenCount);
                if (HandleCount == 0) CFN.FileData = null; //No more handle on the file, we free its content
                DebugEnd(OperationId, CFN, $"STATUS_SUCCESS  - Handle {HandleCount}");
            }
        }

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
                            //Fichier
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
                            RepositoryActionPerformed?.Invoke(this, new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_FAILED" });
                            DebugEnd(OperationId, CFN, $"Exception : {ex.Message}");
                        }
                    }
                    else
                    {
                        try
                        {
                            //Répertoire
                            Proxy.DeleteFolder(CFN.RepositoryPath).GetAwaiter().GetResult();
                            Cache.DeleteFileNode(CFN);
                            CFN.IsDeleted = true;
                            CFN.HasUnflushedData = false;
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_SUCCESS");
                        }
                        catch (Exception)
                        {
                            L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Delete", File = CFN.LocalPath, Result = "STATUS_FAILED" };
                            RepositoryActionPerformed?.Invoke(this, L);
                            DebugEnd(OperationId, null, "STATUS_FAILED");
                        }
                    }
                }
                else if ((Flags & CleanupSetAllocationSize) != 0 || (Flags & CleanupSetArchiveBit) != 0 || (Flags & CleanupSetLastWriteTime) != 0)
                {
                    if (this.FlushMode == FlushMode.FlushAtCleanup && !CFN.IsDeleted)
                    {
                        var Proxy = new WebDavClient2();
                        if (CFN.HasUnflushedData)
                        {
                            try
                            {
                                if (CFN.FileData != null)
                                {
                                    if (!Proxy.Upload(FileNode.GetRepositoryParentPath(CFN.RepositoryPath), new MemoryStream(CFN.FileData, 0, (int)CFN.FileInfo.FileSize), CFN.Name).GetAwaiter().GetResult())
                                    {
                                        CFN.GenerateLocalCopy();
                                        throw new Exception("Upload failed");
                                    }
                                }
                                CFN.HasUnflushedData = false;
                                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Cleanup Flush", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                                RepositoryActionPerformed?.Invoke(this, L);
                                DebugEnd(OperationId, null, "STATUS_SUCCESS");
                            }
                            catch (Exception)
                            {
                                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = $"Cleanup Flush", File = CFN.LocalPath, Result = "STATUS_FAILED", LocalTemporaryPath = CFN.TemporaryLocalCopyPath };
                                RepositoryActionPerformed?.Invoke(this, L);
                                DebugEnd(OperationId, null, "STATUS_FAILED");
                                //TODO : Remove from Cache ??
                                return;
                            }
                        }
                    }
                }

                CFN.FlushUpload();

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

        private async Task AsyncRead(Task Task, String OperationId, FileNode CFN, IntPtr Buffer, UInt64 Offset, UInt32 Length, UInt64 RequestHint)
        {
            UInt32 BytesTransferred;
            byte[] FileData = null;

            if (Task != null)
            {
                await Task;
            }

            try
            {
                FileData = await DownloadClient.DownloadPartial(CFN.RepositoryPath, (long)Offset, (long)Offset + Length - 1);
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                RepositoryAuthenticationFailed?.Invoke(this, null);
                Cache.Clear();
                Host.SendReadResponse(RequestHint, STATUS_NETWORK_UNREACHABLE, 0);
                return;
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 416)
            {
                DebugEnd(OperationId, CFN, $"STATUS_END_OF_FILE - {ex.Message}");
                Host.SendReadResponse(RequestHint, STATUS_END_OF_FILE, 0);
                return;
            }
            catch (HttpRequestException ex)
            {
                DebugEnd(OperationId, CFN, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                Host.SendReadResponse(RequestHint, STATUS_NETWORK_UNREACHABLE, 0);
                return;
            }
            catch (Exception ex)
            {
                DebugEnd(OperationId, CFN, $"STATUS_NETWORK_UNREACHABLE - {ex.Message}");
                Host.SendReadResponse(RequestHint, STATUS_NETWORK_UNREACHABLE, 0);
                return;
            }

            if (Offset == 0 && (ulong)FileData.LongLength == CFN.FileInfo.FileSize)
            {
                CFN.FileData = FileData;
            }
            BytesTransferred = (uint)FileData.Length;
            if (FileData == null)
            {
                DebugEnd(OperationId, CFN, "STATUS_OBJECT_NAME_NOT_FOUND");
                Host.SendReadResponse(RequestHint, STATUS_OBJECT_NAME_NOT_FOUND, BytesTransferred);
                return;
            }
            Marshal.Copy(FileData, 0, Buffer, (int)BytesTransferred);

            DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
            Host.SendReadResponse(RequestHint, STATUS_SUCCESS, BytesTransferred);
        }

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

                if (CFN.PendingUpload(Offset)) {
                    DebugEnd(OperationId, CFN, "STATUS_END_OF_FILE");
                    return STATUS_END_OF_FILE;
                }

                byte[] FileData = null;
                if (CFN.FileData == null)
                {
                    DownloadTask = AsyncRead(DownloadTask, OperationId, CFN, Buffer, Offset, Length, Host.GetOperationRequestHint());
                    return STATUS_PENDING;
                }
                else
                {
                    FileData = CFN.FileData;
                    UInt64 FileSize = (UInt64)FileData.LongLength;

                    if (Offset >= FileSize)
                    {
                        BytesTransferred = default(UInt32);
                        DebugEnd(OperationId, CFN, "STATUS_END_OF_FILE");
                        return STATUS_END_OF_FILE;
                    }

                    UInt64 EndOffset = Offset + Length;
                    if (EndOffset > FileSize) EndOffset = FileSize;

                    BytesTransferred = (UInt32)(EndOffset - Offset);
                }

                Marshal.Copy(FileData, (int)Offset, Buffer, (int)BytesTransferred);

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

        async Task AsyncWrite(Task Task, String OperationId, FileNode CFN, byte[] Data, UInt32 Length, UInt64 RequestHint)
        {
            LogListItem L;

            if (Task != null)
            {
                await Task;
            }

            try
            {
                await Task.Run(() => CFN.Upload(Data, Length));
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_SUCCESS" };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, CFN, "STATUS_SUCCESS");
                Host.SendWriteResponse(RequestHint, STATUS_SUCCESS, Length, ref CFN.FileInfo);
            }
            catch (Exception)
            {
                Cache.InvalidateFileNode(CFN);
                L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Write Flush", File = CFN.LocalPath, Result = "STATUS_UNEXPECTED_IO_ERROR" };
                RepositoryActionPerformed?.Invoke(this, L);
                DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR");
                Host.SendWriteResponse(RequestHint, STATUS_UNEXPECTED_IO_ERROR, Length, ref CFN.FileInfo);
            }
        }

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
                var FileData = CFN.FileData;
                var FileDataOffset = Offset;
                if (this.FlushMode == FlushMode.FlushAtWrite)
                {
                    FileData = new byte[BytesTransferred];
                    CFN.FileData = (Offset == 0 && BytesTransferred == CFN.FileInfo.FileSize) ? FileData : null;
                    FileDataOffset = 0;
                }

                try
                {
                    Marshal.Copy(Buffer, FileData, (int)FileDataOffset, (int)BytesTransferred);
                }
                catch (Exception ex)
                {
                    logger.Trace($"{CFN.ObjectId} Write Exception {ex.Message}");
                    BytesTransferred = default(UInt32);
                    FileInfo = default(FileInfo);
                    DebugEnd(OperationId, CFN, "STATUS_UNEXPECTED_IO_ERROR");
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                if (this.FlushMode == FlushMode.FlushAtWrite)
                {
                    try
                    {
                        if (CFN.ContinueUpload(Offset, BytesTransferred))
                        {
                            ContinuedTask = AsyncWrite(ContinuedTask, OperationId, CFN, FileData, BytesTransferred, Host.GetOperationRequestHint());
                            return STATUS_PENDING;
                        }
                        else
                        {
                            UploadTask = CFN.Upload(new WebDavClient2(Timeout.InfiniteTimeSpan), FileData, Offset, BytesTransferred);
                            ContinuedTask = null;
                        }
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
                                Proxy.DeleteFile(FileNode.ConvertLocalPathToRepositoryPath(NewFileName));
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
                catch (Exception)
                {
                    L = new LogListItem() { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Object = CFN.ObjectId, Method = "Rename", File = $"{FileName} -> {NewFileName}", Result = "STATUS_CANNOT_MAKE" };
                    RepositoryActionPerformed?.Invoke(this, L);
                    DebugEnd(OperationId, CFN, $"STATUS_CANNOT_MAKE");
                    return FileSystemBase.STATUS_CANNOT_MAKE;
                }

                if ((CFN.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) == 0)
                {
                    //Fichier
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
                        RepositoryAuthenticationFailed?.Invoke(this, null);
                        Cache.Clear();
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
                    //Répertoire
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
                        RepositoryAuthenticationFailed?.Invoke(this, null);
                        Cache.Clear();
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

        public override Int32 Flush(
            Object FileNode0,
            Object FileDesc,
            out FileInfo FileInfo)
        {
            DateTime StartTime = DateTime.Now;
            FileNode CFN = (FileNode)FileNode0;

            lock (CFN.OperationLock)
            {
                String OperationId = Guid.NewGuid().ToString();
                DebugStart(OperationId, CFN);

                FileInfo = (null != CFN ? CFN.FileInfo : default(FileInfo));

                DebugEnd(OperationId, CFN, "STATUS_SUCCESS");

                return STATUS_SUCCESS;
            }
        }

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

                Int32 Result = SetFileSizeInternal(CFN, NewSize, SetAllocationSize);
                FileInfo = Result >= 0 ? CFN.FileInfo : default(FileInfo);

                logger.Trace($"{CFN.ObjectId} SetFileSize File {CFN.LocalPath}. New Size {NewSize}. Allocation size {SetAllocationSize}");

                /*
                FileNode FileNode = (FileNode)FileNode0;
                Int32 Result;

                Result = SetFileSizeInternal(FileNode, NewSize, SetAllocationSize);
                FileInfo = 0 <= Result ? FileNode.GetFileInfo() : default(FileInfo);
                */

                return STATUS_SUCCESS;
            }
        }

        private Int32 SetFileSizeInternal(
            FileNode FileNode,
            UInt64 NewSize,
            Boolean SetAllocationSize)
        {
            try
            {
                if (SetAllocationSize)
                {
                    if (FileNode.FileInfo.AllocationSize != NewSize)
                    {
                        byte[] FileData = null;
                        if (this.FlushMode == FlushMode.FlushAtCleanup && NewSize != 0)
                        {
                            try
                            {
                                FileData = new byte[NewSize];
                            }
                            catch
                            {
                                return STATUS_INSUFFICIENT_RESOURCES;
                            }
                            int CopyLength = (int)Math.Min(FileNode.FileInfo.AllocationSize, NewSize);
                            if (CopyLength != 0) Array.Copy(FileNode.FileData, FileData, CopyLength);
                        }

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

                        if (this.FlushMode == FlushMode.FlushAtCleanup && NewSize > FileNode.FileInfo.FileSize)
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
                logger.Error($"{FileNode.ObjectId} SetFileSizeInternal {ex.Message} {ex.StackTrace}");
            }

            return STATUS_SUCCESS;
        }

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

        public override void Unmounted(object Host)
        {
            Cache.Clear();
            base.Unmounted(Host);
        }

        private void TraceStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
            logger.Trace($"{OperationId}|{Caller}|Start|{JsonConvert.SerializeObject(CFN)}");
        }

        private void TraceStart(String OperationId, String FileName, [CallerMemberName]string Caller = "")
        {
            logger.Trace($"{OperationId}|{Caller}|Start|{FileName}");
        }

        private void TraceEnd(String OperationId, FileNode CFN, String Result, [CallerMemberName]string Caller = "")
        {
            logger.Trace($"{OperationId}|{Caller}|End|{Result}");
        }

        private void DebugStart(string OperationId, FileNode CFN, [CallerMemberName]string Caller = "")
        {
            logger.Debug($"{OperationId}|{Caller}|Start|{JsonConvert.SerializeObject(CFN)}");
        }

        private void DebugStart(String OperationId, String FileName, [CallerMemberName]string Caller = "")
        {
            logger.Debug($"{OperationId}|{Caller}|Start|{FileName}");
        }

        private void DebugEnd(String OperationId, FileNode CFN, String Result, [CallerMemberName]string Caller = "")
        {
            logger.Debug($"{OperationId}|{Caller}|End|{Result}");
        }
    }

    public static class Extension
    {
        public static void RenameKey<TKey, TValue>(this IDictionary<TKey, TValue> dic,
                                      TKey fromKey, TKey toKey)
        {
            TValue value = dic[fromKey];
            dic.Remove(fromKey);
            dic[toKey] = value;
        }
    }

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