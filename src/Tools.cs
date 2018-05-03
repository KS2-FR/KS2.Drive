using KS2Drive.Config;
using System;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KS2Drive
{
    public class Tools
    {
        public static string Protect(string str)
        {
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            byte[] data = Encoding.ASCII.GetBytes(str);
            string protectedData = Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
            return protectedData;
        }

        public static string Unprotect(string str)
        {
            byte[] protectedData = Convert.FromBase64String(str);
            byte[] entropy = Encoding.ASCII.GetBytes(Assembly.GetExecutingAssembly().FullName);
            string data = Encoding.ASCII.GetString(ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser));
            return data;
        }

        public static void LoadProxy(Configuration C)
        {
            if (C == null || !C.UseHTTPProxy)
            {
                WebRequest.DefaultWebProxy = null;
            }
            else
            {
                if (C.UseProxyAuthentication)
                {
                    WebRequest.DefaultWebProxy = new WebProxy(C.ProxyURL, false, null, new NetworkCredential(C.ProxyLogin, C.ProxyPassword));
                }
                else
                {
                    WebRequest.DefaultWebProxy = new WebProxy(C.ProxyURL, false);
                }
            }
        }
    }
}
