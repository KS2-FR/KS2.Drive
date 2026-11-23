using KS2Drive.Config;
using Microsoft.Win32;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Linq;

namespace KS2Drive
{
    /// <summary>
    /// Miscellaneous helpers: configuration encryption, proxy setup, certificate lookup and installed product detection.
    /// </summary>
    public class Tools
    {
        /// <summary>
        /// DPAPI entropy used to encrypt the configuration. It must never change, otherwise existing configurations can no longer be decrypted.
        /// </summary>
        private const String ConfigurationEntropy = "KS2Drive.Configuration";

        /// <summary>
        /// Assembly versions whose full name was used as entropy by previous releases (up to 0.8.6.1), so that their
        /// configuration can still be decrypted and migrated. Releases 0.8.4 to 0.8.6.1 all kept the 0.8.4.0 assembly version.
        /// This list is frozen: it must not be updated when the application version changes.
        /// </summary>
        private static readonly String[] LegacyAssemblyVersions = { "0.8.4.0", "0.8.3.0", "0.8.2.0", "0.8.1.0", "0.8.0.0", "1.0.0.0" };

        /// <summary>
        /// Encrypts a string with DPAPI for the current user, using a fixed entropy (independent of the application version).
        /// The string is converted with UTF-8 encoding.
        /// </summary>
        /// <returns>The encrypted data as a Base64 string.</returns>
        public static string Protect(string str)
        {
            byte[] entropy = Encoding.UTF8.GetBytes(ConfigurationEntropy);
            byte[] data = Encoding.UTF8.GetBytes(str);
            string protectedData = Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
            return protectedData;
        }

        /// <summary>
        /// Decrypts a Base64 string produced by <see cref="Protect"/> for the current user.
        /// Also accepts the legacy format of previous releases (entropy = assembly full name, ASCII encoding).
        /// </summary>
        /// <param name="str">The encrypted data as a Base64 string.</param>
        /// <param name="IsLegacyFormat">Receives true if the data was in the legacy format and should be encrypted again with <see cref="Protect"/>.</param>
        /// <exception cref="CryptographicException">The data cannot be decrypted (other user, other machine, corrupted file...).</exception>
        public static string Unprotect(string str, out bool IsLegacyFormat)
        {
            byte[] protectedData = Convert.FromBase64String(str);

            //Current format. UTF-8 also decodes the legacy ASCII content correctly
            IsLegacyFormat = false;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedData, Encoding.UTF8.GetBytes(ConfigurationEntropy), DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
            }

            //Legacy format of previous releases: the entropy was the full name of the assembly, which contained its version.
            //Only the frozen list of versions already released is tried: the current version of the application is never used
            IsLegacyFormat = true;
            foreach (String LegacyEntropy in LegacyAssemblyVersions.Select(v => $"KS2Drive, Version={v}, Culture=neutral, PublicKeyToken=null"))
            {
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedData, Encoding.ASCII.GetBytes(LegacyEntropy), DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException)
                {
                }
            }

            throw new CryptographicException("The configuration cannot be decrypted");
        }

        /// <summary>
        /// Sets the process-wide <see cref="WebRequest.DefaultWebProxy"/> from the configuration: no proxy (mode 0 or null configuration),
        /// system proxy with default credentials (mode 1), or the user defined proxy with optional credentials (other modes).
        /// </summary>
        public static void LoadProxy(Configuration C)
        {
            if (C == null || C.HTTPProxyMode == 0)
            {
                WebRequest.DefaultWebProxy = null;
            }
            else if (C.HTTPProxyMode == 1)
            {
                WebRequest.DefaultWebProxy = WebRequest.GetSystemWebProxy();
                WebRequest.DefaultWebProxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
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

        /// <summary>
        /// Looks up a certificate by serial number in a Windows certificate store.
        /// </summary>
        /// <param name="CertStoreName">Name of a <see cref="StoreName"/> value.</param>
        /// <param name="CertStoreLocation">Name of a <see cref="StoreLocation"/> value.</param>
        /// <param name="Serial">Certificate serial number.</param>
        /// <returns>The first matching certificate (validity is not checked), or null if none is found or the store names are invalid.</returns>
        public static X509Certificate2 FindCertificate(String CertStoreName, String CertStoreLocation, String Serial)
        {
            if (String.IsNullOrEmpty(Serial)) return null;

            //Enum.TryParse also accepts any number (e.g. "999"), which is not a valid store: check that the value is defined
            if (Enum.TryParse(CertStoreName, out StoreName StoreNameParsed) && Enum.IsDefined(typeof(StoreName), StoreNameParsed)
                && Enum.TryParse(CertStoreLocation, out StoreLocation StoreLocationParsed) && Enum.IsDefined(typeof(StoreLocation), StoreLocationParsed))
            {
                X509Store store = new X509Store(StoreNameParsed, StoreLocationParsed);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    var CertificateCollection = store.Certificates.Find(X509FindType.FindBySerialNumber, Serial, false);
                    if (CertificateCollection.Count > 0)
                    {
                        return CertificateCollection[0];
                    }
                }
                finally
                {
                    store.Close();
                }
            }

            return null;
        }

        /// <summary>
        /// Checks whether an MSI product is installed by looking for its product code under the Uninstall registry keys
        /// of the current user and of the local machine, in both the 64-bit and the 32-bit registry views (whatever the
        /// bitness of this process). Missing keys are ignored.
        /// </summary>
        /// <param name="ProductCode">MSI product code, including braces (e.g. "{...}").</param>
        public static bool IsMsiIntalled(String ProductCode)
        {
            const String UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            foreach (RegistryHive Hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView View in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey BaseKey = RegistryKey.OpenBaseKey(Hive, View))
                        using (RegistryKey UninstallKey = BaseKey.OpenSubKey(UninstallKeyPath))
                        {
                            if (UninstallKey == null) continue; //The key may not exist (e.g. for the current user)
                            if (UninstallKey.GetSubKeyNames().Any(x => x.Equals(ProductCode, StringComparison.OrdinalIgnoreCase))) return true;
                        }
                    }
                    catch (Exception)
                    {
                        //Unreadable view or key: try the next one
                    }
                }
            }

            return false;
        }
    }
}
