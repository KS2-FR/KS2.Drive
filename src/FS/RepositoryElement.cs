using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive
{
    public class RepositoryElement
    {
        public string Href { get; set; }
        public DateTime? CreationDate { get; set; }
        public string Etag { get; set; }
        public bool IsHidden { get; set; }
        public bool IsCollection { get; set; }
        public string ContentType { get; set; }
        public DateTime? LastModified { get; set; }
        public string DisplayName { get; set; }
        public long? ContentLength { get; set; }

        public RepositoryElement(WebDAVClient.Model.Item Item)
        {
            this.Href = Item.Href;
            this.CreationDate = Item.CreationDate;
            this.Etag = Item.Etag;
            this.IsHidden = Item.IsHidden;
            this.IsCollection = Item.IsCollection;
            this.ContentType = Item.ContentType;
            this.LastModified = Item.LastModified;
            this.DisplayName = Item.DisplayName;
            this.ContentLength = Item.ContentLength;
        }
    }
}
