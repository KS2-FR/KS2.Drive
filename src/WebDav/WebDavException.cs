using System;
using System.Runtime.Serialization;

namespace KS2Drive.WebDav
{
    /// <summary>
    /// A WebDAV request answered with an unexpected HTTP status (401 credentials rejected, 404 not found, 409 conflict,
    /// 412 precondition failed...). Transport errors (server unreachable, timeout) are reported as HttpRequestException.
    /// </summary>
    [Serializable]
    public class WebDavException : Exception
    {
        /// <summary>
        /// HTTP status code answered by the server.
        /// </summary>
        public int StatusCode { get; }

        public WebDavException(int StatusCode, String Message) : base(Message)
        {
            this.StatusCode = StatusCode;
        }

        protected WebDavException(SerializationInfo Info, StreamingContext Context) : base(Info, Context)
        {
            StatusCode = Info.GetInt32(nameof(StatusCode));
        }

        public override void GetObjectData(SerializationInfo Info, StreamingContext Context)
        {
            base.GetObjectData(Info, Context);
            Info.AddValue(nameof(StatusCode), StatusCode);
        }

        public override String ToString() => $"HTTP {StatusCode}: {base.ToString()}";
    }
}
