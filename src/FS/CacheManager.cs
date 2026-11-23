using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebDAVClient.Helpers;

namespace KS2Drive.FS
{
    /// <summary>
    /// In-memory cache of the remote repository tree, keyed by local path.
    /// Keeps the known <see cref="FileNode"/> objects, remembers paths recently found to be missing,
    /// and refreshes folder listings from the WebDAV server in background threads.
    /// </summary>
    public class CacheManager
    {
        /// <summary>
        /// Raised whenever the cache content changes (node added, renamed or removed).
        /// </summary>
        public EventHandler CacheRefreshed;

        private Int32 CacheDurationInSeconds = 15; //Amount of seconds the information stored in the cache in considered as still valid
        private CacheMode _mode;
        private bool _PreLoadFoldersInCache;

        private object RunningRefreshActionListLock = new object();
        private List<String> RunningRefreshActionList = new List<string>();

        /// <summary>
        /// Creates a cache manager.
        /// </summary>
        /// <param name="mode">When <see cref="CacheMode.Disabled"/>, every cache operation is a no-op and listings always go to the server.</param>
        /// <param name="PreLoadFoldersInCache">When true, listing a folder also schedules a background listing of its not-yet-parsed sub-folders.</param>
        public CacheManager(CacheMode mode, bool PreLoadFoldersInCache)
        {
            this._mode = mode;
            this._PreLoadFoldersInCache = PreLoadFoldersInCache;
        }

        /// <summary>
        /// Starts a background thread that refreshes the cached content of the given folder from the server,
        /// unless a refresh for the same path is already planned or running.
        /// </summary>
        private void AddRefreshTask(FileNode CFN)
        {
            lock (RunningRefreshActionListLock)
            {
                if (RunningRefreshActionList.Contains(CFN.LocalPath)) return; //An action is already planned or in progress for this path
                RunningRefreshActionList.Add(CFN.LocalPath);
                new Thread(() => InternalRefreshFolderCacheContent(CFN)).Start();
            }
        }

        /// <summary>
        /// Removes the folder from the list of running refresh actions so that it can be refreshed again.
        /// </summary>
        private void RemoveRefreshTask(FileNode CFN)
        {
            lock (RunningRefreshActionListLock)
            {
                RunningRefreshActionList.Remove(CFN.LocalPath);
            }
        }

        private static object CacheLock = new object();

        //TODO : Add periodic MissingFileCache cleanup
        private Dictionary<String, DateTime> MissingFileCache = new Dictionary<string, DateTime>(); //Store the files that we know for sure that they do not exist
        private Dictionary<String, FileNode> FileNodeCache = new Dictionary<string, FileNode>();

        /// <summary>
        /// Looks in the cache for the FileNode matching the local path. Does not take the cache lock:
        /// the caller is expected to hold it (see <see cref="Lock"/>).
        /// </summary>
        /// <returns>
        /// The cached node if found; otherwise a null node with <c>IsNonExistent</c> set to true when the path
        /// was recorded as missing less than <c>CacheDurationInSeconds</c> ago, false when its state is unknown.
        /// </returns>
        public (FileNode node, bool IsNonExistent) GetFileNodeNoLock(String FileOrFolderLocalPath)
        {
            if (_mode == CacheMode.Disabled) return (null, false);

            if (!FileNodeCache.ContainsKey(FileOrFolderLocalPath))
            {
                //Is there a valid missing file entry for this file
                if (MissingFileCache.ContainsKey(FileOrFolderLocalPath) && (DateTime.Now - MissingFileCache[FileOrFolderLocalPath]).TotalSeconds <= CacheDurationInSeconds)
                {
                    //Less than (CacheDurationInSeconds) seconds ago, the file was not existing. We send the same answer
                    return (null, true);
                }
                else
                {
                    return (null, false);
                }
            }
            else
            {
                return (FileNodeCache[FileOrFolderLocalPath], false);
            }
        }

        /// <summary>
        /// Adds a node to the cache (ignored if its path is already cached) and removes the path from the
        /// known-missing list. Does not take the cache lock: the caller is expected to hold it.
        /// </summary>
        public void AddFileNodeNoLock(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            if (FileNodeCache.ContainsKey(node.LocalPath)) return; //TODO : monitor which condition can lead to this

            FileNodeCache.Add(node.LocalPath, node);
            MissingFileCache.Remove(node.LocalPath); //Remove the file from the known missing files list
            CacheRefreshed?.Invoke(this, null);
        }

        /// <summary>
        /// Re-keys a cached node from its previous local path to its new local path.
        /// Only the dictionary key is changed; the node's own path properties are not updated.
        /// </summary>
        public void RenameFileNodeKey(String PreviousKey, String NewKey)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.RenameKey(PreviousKey, NewKey);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        /// <summary>
        /// After a folder rename, updates the local path, repository path and cache key of every cached
        /// descendant of the folder. The folder's own entry is not handled here (see <see cref="RenameFileNodeKey"/>).
        /// </summary>
        /// <param name="OldFolderName">Previous local path of the folder.</param>
        /// <param name="NewFolderName">New local path of the folder.</param>
        public void RenameFolderSubElements(String OldFolderName, String NewFolderName)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                foreach (var FolderSubElement in FileNodeCache.Where(x => x.Key.StartsWith(OldFolderName + "\\")).ToList())
                {
                    String OldKeyName = FolderSubElement.Key;
                    FolderSubElement.Value.LocalPath = NewFolderName + FolderSubElement.Value.LocalPath.Substring(OldFolderName.Length);
                    FolderSubElement.Value.RepositoryPath = FileNode.ConvertLocalPathToRepositoryPath(FolderSubElement.Value.LocalPath);
                    String NewKeyName = FolderSubElement.Value.LocalPath;

                    FileNodeCache.RenameKey(OldKeyName, NewKeyName);

                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Removes a node from the cache. If the node is a directory, all its cached descendants are removed too.
        /// </summary>
        public void DeleteFileNode(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                if ((node.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
                {
                    foreach (var s in FileNodeCache.Where(x => x.Key.StartsWith(node.LocalPath + "\\")).ToList())
                    {
                        FileNodeCache.Remove(s.Key);
                    }
                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Removes a single cache entry by its key (local path). Descendants are not removed.
        /// </summary>
        public void DeleteFileEntry(String EntryCode)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.Remove(EntryCode);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Removes a node from the cache and marks its parent folder (the root folder <c>\</c> for an element at the root
        /// of the drive) as not parsed, so that the next listing of the parent is fetched from the server again.
        /// Does nothing more if the parent folder is not cached.
        /// </summary>
        public void InvalidateFileNode(FileNode node)
        {
            if (_mode == CacheMode.Disabled) return;

            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                String ParentFolderPath = node.LocalPath.Substring(0, node.LocalPath.LastIndexOf(@"\"));
                if (ParentFolderPath.Length == 0) ParentFolderPath = @"\"; //Element at the root of the drive: the root folder is cached as "\"
                if (FileNodeCache.TryGetValue(ParentFolderPath, out FileNode ParentFolder)) ParentFolder.IsParsed = false; //The parent may not be cached
            }
        }

        /// <summary>
        /// Returns the folder content as a list of (entry name, FileNode) tuples, sorted by name, including the "." and ".." entries
        /// when applicable.
        /// If the folder has not been parsed yet, its content is listed from the server and cached.
        /// If it has already been parsed, the content is served from the cache and, when older than <c>CacheDurationInSeconds</c>,
        /// refreshed from the server in a background thread so that the next call gets an up-to-date view.
        /// When sub-folder pre-loading is enabled, unparsed sub-folders are also listed in the background.
        /// </summary>
        /// <param name="CurrentFolder">Folder to list.</param>
        /// <param name="Marker">Name of the last entry already returned to WinFSP; only entries sorted after it are returned. Null or empty to start from the beginning.</param>
        /// <returns>Success flag, the entries, and an error message on failure ("401" when authentication failed).</returns>
        public (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) GetFolderContent(FileNode CurrentFolder, String Marker)
        {
            List<FileNode> FileNodeToRefreshList = new List<FileNode>();
            List<Tuple<String, FileNode>> ReturnList = null;

            if (_mode == CacheMode.Disabled)
            {
                var Result = ListRemoteServerFolderContent(CurrentFolder);
                if (!Result.Success) return Result;
                else return (true, Result.Content, null);
            }

            lock (CacheLock)
            {
                if (!CurrentFolder.IsParsed)
                {
                    var Result = ListRemoteServerFolderContent(CurrentFolder);
                    if (!Result.Success) return Result;

                    CurrentFolder.IsParsed = true;
                    CurrentFolder.LastRefresh = DateTime.Now;

                    //Caching the folder content
                    foreach (var Node in Result.Content)
                    {
                        this.AddFileNodeNoLock(Node.Item2);
                    }

                    //Handling . & ..
                    if (!FileNode.IsRepositoryRootPath(CurrentFolder.RepositoryPath))
                    {
                        //if this is not the root directory add the dot entries
                        if (Marker == null) Result.Content.Add(new Tuple<String, FileNode>(".", CurrentFolder));

                        if (Marker == null || Marker == ".")
                        {
                            String ParentPath = FileNode.ConvertRepositoryPathToLocalPath(FileNode.GetRepositoryParentPath(CurrentFolder.RepositoryPath));
                            if (ParentPath != null)
                            {
                                if (FileNodeCache.ContainsKey(ParentPath))
                                {
                                    Result.Content.Add(new Tuple<String, FileNode>("..", FileNodeCache[ParentPath]));
                                }
                            }
                        }
                    }

                    ReturnList = Result.Content;
                }
                else
                {
                    String FolderNameForSearch = CurrentFolder.LocalPath;
                    if (FolderNameForSearch != "\\") FolderNameForSearch += "\\";
                    ReturnList = new List<Tuple<String, FileNode>>();
                    ReturnList.Add(new Tuple<string, FileNode>(".", CurrentFolder));
                    //TODO : Add .. from cache
                    ReturnList.AddRange(FileNodeCache.Where(x => x.Key != CurrentFolder.LocalPath && x.Key.StartsWith($"{FolderNameForSearch}") && x.Key.LastIndexOf('\\').Equals(FolderNameForSearch.Length - 1)).Select(x => new Tuple<String, FileNode>(x.Value.Name, x.Value)));
                    if ((DateTime.Now - CurrentFolder.LastRefresh).TotalSeconds > CacheDurationInSeconds) FileNodeToRefreshList.Add(CurrentFolder); //Refresh current directory if the cache is too old
                }

                //Sort list by path (mandatory if we want to handle a potential marker correctly)
                ReturnList = ReturnList.OrderBy(x => x.Item1).ToList();

                if (!String.IsNullOrEmpty(Marker)) //Dealing with potential marker
                {
                    var WantedTuple = ReturnList.FirstOrDefault(x => x.Item1.Equals(Marker));
                    var WantedTupleIndex = ReturnList.IndexOf(WantedTuple);
                    if (WantedTupleIndex + 1 < ReturnList.Count) ReturnList = ReturnList.GetRange(WantedTupleIndex + 1, ReturnList.Count - 1 - WantedTupleIndex);
                    else ReturnList.Clear();
                }

                if (this._PreLoadFoldersInCache)
                {
                    foreach (var FolderNode in ReturnList.Where(x => (x.Item2.FileInfo.FileAttributes & (UInt32)System.IO.FileAttributes.Directory) != 0))
                    {
                        if (FolderNode.Item1 == "." || FolderNode.Item1 == "..") continue; //Bypass special folders
                        if (!FileNodeCache[FolderNode.Item2.LocalPath].IsParsed) FileNodeToRefreshList.Add(FolderNode.Item2); //Pre-loading of sub-folders of current folder
                    }
                }
            }

            foreach (var FileNodeToRefresh in FileNodeToRefreshList)
            {
                AddRefreshTask(FileNodeToRefresh);
            }

            return (true, ReturnList, null);
        }

        /// <summary>
        /// Empties the node cache and releases it (the dictionary is set to null).
        /// Background refresh threads check for this and stop updating the cache.
        /// </summary>
        public void Clear()
        {
            FileNodeCache.Clear();
            FileNodeCache = null;
        }

        /// <summary>
        /// Acquires the cache lock. Must be paired with <see cref="Unlock"/>; required around the *NoLock methods.
        /// </summary>
        public void Lock()
        {
            Monitor.Enter(CacheLock);
        }

        /// <summary>
        /// Releases the cache lock acquired with <see cref="Lock"/>.
        /// </summary>
        public void Unlock()
        {
            Monitor.Exit(CacheLock);
        }

        /// <summary>
        /// Background refresh of a folder: lists it from the server, adds new children to the cache, updates the
        /// file information of known children (unless they hold unflushed data) and removes cached children that
        /// no longer exist on the server.
        /// </summary>
        private void InternalRefreshFolderCacheContent(FileNode CFN)
        {
            var Result = ListRemoteServerFolderContent(CFN);
            RemoveRefreshTask(CFN); //Server parsing operation has been made. The filenode status is now parsed
            if (!Result.Success) return;

            lock (CacheLock)
            {
                CFN.IsParsed = true;
                CFN.LastRefresh = DateTime.Now;

                if (FileNodeCache == null) return; //Handle the case when the thread is still performing while the class has been unloaded

                String Filter = FileNode.IsRepositoryRootPath(CFN.RepositoryPath) ? "\\" : CFN.LocalPath + "\\";

                //Refresh from server result
                foreach (var Node in Result.Content)
                {
                    if (!FileNodeCache.ContainsKey(Node.Item2.LocalPath))
                    {
                        this.AddFileNodeNoLock(Node.Item2);
                    }
                    else
                    {
                        //Refresh node with updated properties
                        //TODO : Should more properties be updated ?
                        var KnownCachedItem = FileNodeCache[Node.Item2.LocalPath];
                        if (!KnownCachedItem.HasUnflushedData) KnownCachedItem.FileInfo = Node.Item2.FileInfo;
                    }
                }

                //Remove the FileNodeCache entries that are no longer in Result.Content
                foreach (var s in FileNodeCache.Where(x => x.Key != CFN.LocalPath && x.Key.StartsWith(Filter) && x.Key.LastIndexOf('\\').Equals(Filter.Length - 1)).ToList())
                {
                    if (Result.Content.FirstOrDefault(x => x.Item2.LocalPath.Equals(s.Value.LocalPath)) == null)
                    {
                        DeleteFileNode(s.Value);
                    }
                }

                CFN.LastRefresh = DateTime.Now;
            }
        }

        /// <summary>
        /// Lists the direct children of a folder on the WebDAV server (the folder's own entry is excluded).
        /// </summary>
        /// <returns>Success flag, the (name, FileNode) children, and an error message on failure ("401" when authentication failed).</returns>
        private (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) ListRemoteServerFolderContent(FileNode CFN)
        {
            var Proxy = new WebDavClient2();
            List<Tuple<String, FileNode>> ChildrenFileNames = new List<Tuple<String, FileNode>>();

            IEnumerable<WebDAVClient.Model.Item> ItemsInFolder;

            try
            {
                ItemsInFolder = Proxy.List(CFN.RepositoryPath).GetAwaiter().GetResult();
            }
            catch (WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                return (false, null, "401");
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }

            foreach (var Child in ItemsInFolder)
            {
                var Element = new FileNode(Child);
                if (Element.RepositoryPath.Equals(CFN.RepositoryPath)) continue; //Skip the entry matching the listed folder itself
                ChildrenFileNames.Add(new Tuple<string, FileNode>(Element.Name, Element));
            }

            return (true, ChildrenFileNames, null);
        }

        /// <summary>
        /// Records (or re-timestamps) a local path as known to be missing on the server, so that lookups within
        /// <c>CacheDurationInSeconds</c> can answer "not found" without a server round-trip. Does not take the cache lock.
        /// </summary>
        public void AddMissingFileNoLock(String FileOrFolderPath)
        {
            if (_mode == CacheMode.Disabled) return;
            MissingFileCache.Remove(FileOrFolderPath);
            MissingFileCache.Add(FileOrFolderPath, DateTime.Now);
        }
    }
}
