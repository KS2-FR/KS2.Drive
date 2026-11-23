using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.Log
{
    /// <summary>
    /// One entry of the operation log displayed in the main window (one file system operation performed against the repository).
    /// </summary>
    public class LogListItem
    {
        /// <summary>Timestamp of the operation, preformatted as text.</summary>
        public String Date { get; set; }
        /// <summary>Kind of object the operation applied to.</summary>
        public String Object { get; set; }
        /// <summary>Name of the file system operation performed.</summary>
        public String Method { get; set; }
        /// <summary>Path of the file concerned by the operation.</summary>
        public String File { get; set; }
        /// <summary>Result status of the operation (e.g. "STATUS_SUCCESS").</summary>
        public String Result { get; set; }
        /// <summary>Path of a local temporary copy of the file, kept so it can be recovered after a failed operation; null or empty if none.</summary>
        public String LocalTemporaryPath { get; set; }

        /// <summary>
        /// Indicates whether a local temporary copy exists, so the user can recover the file from the log.
        /// </summary>
        public bool AllowRetryOrRecover
        {
            get
            {
                return !String.IsNullOrEmpty(LocalTemporaryPath);
            }
        }
    }
}