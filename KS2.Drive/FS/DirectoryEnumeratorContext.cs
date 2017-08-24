using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public class DirectoryEnumeratorContext
    {
        public IEnumerator<Tuple<string, FileNode>> Enumerator { get; internal set; }
        public string OperationId { get; internal set; }
    }
}
