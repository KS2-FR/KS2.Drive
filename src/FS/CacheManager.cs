using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public class CacheManager
    {
        public EventHandler CacheRefreshed;

        private object CacheLock = new object();
        public Dictionary<String, FileNode> FileNodeCache = new Dictionary<string, FileNode>();

        public List<FileNode> CacheGetFolderContent(String FolderName)
        {
            lock (CacheLock)
            {
                if (!FileNodeCache.ContainsKey(FolderName) || !FileNodeCache[FolderName].IsParsed) return null;

                if (FolderName != "\\") FolderName += "\\";

                List<FileNode> ReturnList = new List<FileNode>();
                ReturnList.AddRange(FileNodeCache.Where(x => x.Key != FolderName && x.Key.StartsWith($"{FolderName}") && x.Key.LastIndexOf('\\').Equals(FolderName.Length - 1)).Select(x => x.Value));
                return ReturnList;
            }
        }

        internal FileNode GetFileNodeNoLock(String FileOrFolderLocalPath)
        {
            if (!FileNodeCache.ContainsKey(FileOrFolderLocalPath)) return null;
            else return FileNodeCache[FileOrFolderLocalPath];
        }

        public FileNode GetFileNode(String FileOrFolderLocalPath)
        {
            lock (CacheLock)
            {
                return GetFileNodeNoLock(FileOrFolderLocalPath);
            }
        }

        internal void AddFileNodeNoLock(FileNode node)
        {
            FileNodeCache.Add(node.LocalPath, node);
            CacheRefreshed?.Invoke(this, null);
        }

        public void AddFileNode(FileNode node)
        {
            lock (CacheLock)
            {
                AddFileNodeNoLock(node);
            }
        }

        public void UpdateFileNodeKey(String PreviousKey, String NewKey)
        {
            lock (CacheLock)
            {
                FileNodeCache.RenameKey(PreviousKey, NewKey);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void RenameFolderSubElements(String OldFolderName, String NewFolderName)
        {
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

        public void DeleteFileNode(FileNode node)
        {
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

        public void InvalidateFileNode(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                String ParentFolderPath = node.LocalPath.Substring(0, node.LocalPath.LastIndexOf(@"\"));
                FileNodeCache[ParentFolderPath].IsParsed = false;
            }
        }

        public void Clear()
        {
            FileNodeCache.Clear();
            FileNodeCache = null;
        }

        internal void Lock()
        {
            Monitor.Enter(CacheLock);
        }

        internal void Unlock()
        {
            Monitor.Exit(CacheLock);
        }
     }
}
