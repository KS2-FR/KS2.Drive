using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    /// <summary>
    /// State kept by WinFSP between successive ReadDirectoryEntry calls of a single directory enumeration.
    /// </summary>
    public class DirectoryEnumeratorContext
    {
        /// <summary>
        /// Enumerator over the directory entries (entry name, matching FileNode) still to be returned.
        /// </summary>
        public IEnumerator<Tuple<string, FileNode>> Enumerator { get; internal set; }
        /// <summary>
        /// Identifier of the enumeration operation, used to correlate debug log start/end entries.
        /// </summary>
        public Guid OperationId { get; internal set; }
    }
}