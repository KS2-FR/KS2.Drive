using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.Log
{
    public class LogListItem
    {
        public String Date { get; set; }
        public String Object { get; set; }
        public String Method { get; set; }
        public String File { get; set; }
        public String Result { get; set; }
        public String LocalTemporaryPath { get; set; }

        public bool AllowRetryOrRecover
        {
            get
            {
                return !String.IsNullOrEmpty(LocalTemporaryPath);
            }
        }
    }
}