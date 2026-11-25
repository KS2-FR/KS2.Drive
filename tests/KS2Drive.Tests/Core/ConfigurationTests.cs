using KS2Drive.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace KS2Drive.Tests.Core
{
    [TestClass]
    public class ConfigurationTests
    {
        private string _TempDir;
        private string _ConfigPath;

        [TestInitialize]
        public void CreateTempDir()
        {
            _TempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KS2DriveTests_Config_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_TempDir);
            _ConfigPath = System.IO.Path.Combine(_TempDir, "config.json");
        }

        [TestCleanup]
        public void DeleteTempDir()
        {
            if (Directory.Exists(_TempDir))
            {
                foreach (var F in Directory.GetFiles(_TempDir)) File.SetAttributes(F, FileAttributes.Normal);
                Directory.Delete(_TempDir, true);
            }
        }

        #region Defaults

        [TestMethod]
        public void Defaults_AreTheDocumentedValues()
        {
            var C = new Configuration();
            Assert.IsNull(C.Path);
            Assert.IsTrue(C.IsConfigured);
            Assert.IsFalse(C.AutoMount);
            Assert.IsFalse(C.AutoStart);
            Assert.IsNull(C.DriveLetter);
            Assert.IsNull(C.ServerURL);
            Assert.AreEqual(0, C.ServerType);
            Assert.IsNull(C.ServerLogin);
            Assert.IsNull(C.ServerPassword);
            Assert.AreEqual(-1, C.KernelCacheMode);
            Assert.AreEqual((int)KS2Drive.FS.KernelCacheMode.DataAndMetaData, C.KernelCacheMode);
            Assert.AreEqual(1, C.FlushMode);
            Assert.AreEqual((int)KS2Drive.FS.FlushMode.FlushAtCleanup, C.FlushMode);
            Assert.IsFalse(C.SyncOps);
            Assert.IsTrue(C.PreLoading);
            Assert.IsFalse(C.MountAsNetworkDrive);
            Assert.AreEqual((short)0, C.HTTPProxyMode);
            Assert.AreEqual("", C.ProxyURL);
            Assert.IsFalse(C.UseProxyAuthentication);
            Assert.AreEqual("", C.ProxyLogin);
            Assert.AreEqual("", C.ProxyPassword);
            Assert.IsFalse(C.UseClientCertForAuthentication);
            Assert.IsNull(C.CertStoreName);
            Assert.IsNull(C.CertStoreLocation);
            Assert.IsNull(C.CertSerial);
        }

        [TestMethod]
        public void IsConfigured_IsNotSerialized()
        {
            var C = new Configuration { IsConfigured = false };
            JObject Json = JObject.Parse(JsonConvert.SerializeObject(C));
            Assert.IsNull(Json.Property("IsConfigured"));
            Assert.IsNotNull(Json.Property("DriveLetter"));
        }

        [TestMethod]
        public void IsConfigured_IsIgnoredWhenDeserializing()
        {
            var C = JsonConvert.DeserializeObject<Configuration>("{\"IsConfigured\":false,\"DriveLetter\":\"X\"}");
            Assert.IsTrue(C.IsConfigured);
            Assert.AreEqual("X", C.DriveLetter);
        }

        #endregion

        #region Save

        [TestMethod]
        public void Save_WritesEncryptedJsonInCurrentFormat()
        {
            var C = FullConfiguration(_ConfigPath);
            C.Save();

            Assert.IsTrue(File.Exists(_ConfigPath));
            string Content = File.ReadAllText(_ConfigPath);
            Assert.IsFalse(Content.Contains("p@ss"), "The password must not be stored in clear text");
            Assert.IsFalse(Content.Contains("ServerURL"), "The file must be encrypted");

            string Json = Tools.Unprotect(Content, out bool IsLegacy);
            Assert.IsFalse(IsLegacy);
            JObject Parsed = JObject.Parse(Json);
            Assert.IsNull(Parsed.Property("IsConfigured"));
            Assert.AreEqual("Y", (string)Parsed["DriveLetter"]);
            Assert.AreEqual("p@ss €", (string)Parsed["ServerPassword"]);
        }

        [TestMethod]
        public void Save_OverwritesExistingFile()
        {
            File.WriteAllText(_ConfigPath, "previous content that is much longer than anything else ..................................................................................................................................................................................................................................................................................................");
            var C = new Configuration { Path = _ConfigPath, DriveLetter = "K" };
            C.Save();
            var Loaded = Configuration.Load(_ConfigPath);
            Assert.IsTrue(Loaded.IsConfigured);
            Assert.AreEqual("K", Loaded.DriveLetter);
        }

        [TestMethod]
        public void Save_NullPath_Throws()
        {
            var C = new Configuration();
            Assert.ThrowsException<ArgumentNullException>(() => C.Save());
        }

        [TestMethod]
        public void Save_MissingDirectory_Throws()
        {
            var C = new Configuration { Path = System.IO.Path.Combine(_TempDir, "missing", "config.json") };
            Assert.ThrowsException<DirectoryNotFoundException>(() => C.Save());
        }

        #endregion

        #region Load

        [TestMethod]
        public void Load_MissingFile_ReturnsNotConfigured()
        {
            var C = Configuration.Load(_ConfigPath);
            AssertNotConfigured(C, _ConfigPath);
            Assert.IsFalse(File.Exists(_ConfigPath), "Load must not create the file");
            Assert.IsFalse(File.Exists(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_CurrentFormat_ReturnsSavedSettings()
        {
            var Saved = FullConfiguration(_ConfigPath);
            Saved.Save();
            string ContentBefore = File.ReadAllText(_ConfigPath);

            var Loaded = Configuration.Load(_ConfigPath);
            Assert.IsTrue(Loaded.IsConfigured);
            Assert.AreEqual(_ConfigPath, Loaded.Path);
            AssertSameSettings(Saved, Loaded);
            Assert.AreEqual(ContentBefore, File.ReadAllText(_ConfigPath), "A file in the current format must not be rewritten");
            Assert.IsFalse(File.Exists(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_AosConfiguration_IsSwitchedToWebDav()
        {
            //AOS can no longer be chosen in the configuration window: a configuration saved in AOS mode must not stay stuck in it
            var Saved = FullConfiguration(_ConfigPath);
            Saved.ServerType = 1; //AOS
            Saved.Save();

            var Loaded = Configuration.Load(_ConfigPath);

            Assert.IsTrue(Loaded.IsConfigured);
            Assert.AreEqual(0, Loaded.ServerType);
            Assert.AreEqual(Saved.ServerURL, Loaded.ServerURL, "Only the mode changes: the URL is kept as is");
        }

        [TestMethod]
        public void Load_PathIsTheLoadedFile()
        {
            FullConfiguration(_ConfigPath).Save();
            string Copy = System.IO.Path.Combine(_TempDir, "copy.json");
            File.Copy(_ConfigPath, Copy);
            var Loaded = Configuration.Load(Copy);
            Assert.AreEqual(Copy, Loaded.Path);
        }

        [TestMethod]
        public void Load_MissingProperties_KeepDefaults()
        {
            File.WriteAllText(_ConfigPath, Tools.Protect("{\"DriveLetter\":\"W\"}"));
            var C = Configuration.Load(_ConfigPath);
            Assert.IsTrue(C.IsConfigured);
            Assert.AreEqual("W", C.DriveLetter);
            Assert.AreEqual(-1, C.KernelCacheMode);
            Assert.AreEqual(1, C.FlushMode);
            Assert.IsTrue(C.PreLoading);
            Assert.AreEqual("", C.ProxyURL);
        }

        [TestMethod]
        public void Load_LegacyFormat_IsLoadedAndMigratedOnDisk()
        {
            var Expected = FullConfiguration(_ConfigPath);
            //Legacy files were ASCII: use ASCII-only values
            Expected.ServerURL = "https://server.example/alfresco/aos";
            Expected.ServerLogin = "user";
            Expected.ServerPassword = "p@ss";
            string Json = JsonConvert.SerializeObject(Expected);
            File.WriteAllText(_ConfigPath, ToolsTests.LegacyProtect(Json, "0.8.4.0"));

            var Loaded = Configuration.Load(_ConfigPath);
            Assert.IsTrue(Loaded.IsConfigured);
            Assert.AreEqual(_ConfigPath, Loaded.Path);
            AssertSameSettings(Expected, Loaded);

            //The file has been rewritten in the current format
            string Migrated = Tools.Unprotect(File.ReadAllText(_ConfigPath), out bool IsLegacy);
            Assert.IsFalse(IsLegacy);
            AssertSameSettings(Expected, JsonConvert.DeserializeObject<Configuration>(Migrated));
            Assert.IsFalse(File.Exists(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_LegacyFormat_ReadOnlyFile_StillLoaded()
        {
            File.WriteAllText(_ConfigPath, ToolsTests.LegacyProtect("{\"DriveLetter\":\"R\"}", "0.8.3.0"));
            File.SetAttributes(_ConfigPath, FileAttributes.ReadOnly);

            var Loaded = Configuration.Load(_ConfigPath);
            Assert.IsTrue(Loaded.IsConfigured);
            Assert.AreEqual("R", Loaded.DriveLetter);

            //Migration failed (read-only): the file is still in the legacy format
            Tools.Unprotect(File.ReadAllText(_ConfigPath), out bool IsLegacy);
            Assert.IsTrue(IsLegacy);
        }

        [TestMethod]
        public void Load_UndecryptableFile_ReturnsNotConfiguredAndKeepsCopy()
        {
            string Garbage = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            File.WriteAllText(_ConfigPath, Garbage);

            var C = Configuration.Load(_ConfigPath);
            AssertNotConfigured(C, _ConfigPath);
            Assert.IsTrue(File.Exists(_ConfigPath + ".unreadable"));
            Assert.AreEqual(Garbage, File.ReadAllText(_ConfigPath + ".unreadable"));
            Assert.AreEqual(Garbage, File.ReadAllText(_ConfigPath), "The original file must not be modified by Load");
        }

        [TestMethod]
        public void Load_NotBase64File_ReturnsNotConfiguredAndKeepsCopy()
        {
            File.WriteAllText(_ConfigPath, "{ not encrypted ! }");
            var C = Configuration.Load(_ConfigPath);
            AssertNotConfigured(C, _ConfigPath);
            Assert.AreEqual("{ not encrypted ! }", File.ReadAllText(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_UnreadableFile_OverwritesPreviousUnreadableCopy()
        {
            File.WriteAllText(_ConfigPath + ".unreadable", "old copy");
            File.WriteAllText(_ConfigPath, "new garbage");
            var C = Configuration.Load(_ConfigPath);
            Assert.IsFalse(C.IsConfigured);
            Assert.AreEqual("new garbage", File.ReadAllText(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_InvalidJson_ReturnsNotConfiguredAndKeepsCopy()
        {
            string Content = Tools.Protect("this is { not json");
            File.WriteAllText(_ConfigPath, Content);
            var C = Configuration.Load(_ConfigPath);
            AssertNotConfigured(C, _ConfigPath);
            Assert.AreEqual(Content, File.ReadAllText(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_NullJson_ReturnsNotConfigured()
        {
            File.WriteAllText(_ConfigPath, Tools.Protect("null"));
            var C = Configuration.Load(_ConfigPath);
            AssertNotConfigured(C, _ConfigPath);
            Assert.IsTrue(File.Exists(_ConfigPath + ".unreadable"));
        }

        [TestMethod]
        public void Load_ThenSaveAfterUnreadable_DoesNotLoseOriginal()
        {
            File.WriteAllText(_ConfigPath, "garbage");
            var C = Configuration.Load(_ConfigPath);
            C.DriveLetter = "Q";
            C.Save();
            Assert.AreEqual("garbage", File.ReadAllText(_ConfigPath + ".unreadable"));
            var Reloaded = Configuration.Load(_ConfigPath);
            Assert.IsTrue(Reloaded.IsConfigured);
            Assert.AreEqual("Q", Reloaded.DriveLetter);
        }

        #endregion

        #region Private methods

        private static Configuration FullConfiguration(string Path)
        {
            return new Configuration
            {
                Path = Path,
                AutoMount = true,
                AutoStart = true,
                DriveLetter = "Y",
                ServerURL = "https://server.example/alfresco/aos/Sites/café",
                ServerType = 1,
                ServerLogin = "useré",
                ServerPassword = "p@ss €",
                KernelCacheMode = 1000,
                FlushMode = 0,
                SyncOps = true,
                PreLoading = false,
                MountAsNetworkDrive = true,
                HTTPProxyMode = 2,
                ProxyURL = "http://proxy:8080",
                UseProxyAuthentication = true,
                ProxyLogin = "plogin",
                ProxyPassword = "ppass",
                UseClientCertForAuthentication = true,
                CertStoreName = "My",
                CertStoreLocation = "CurrentUser",
                CertSerial = "0123AB"
            };
        }

        private static void AssertSameSettings(Configuration Expected, Configuration Actual)
        {
            Assert.AreEqual(Expected.AutoMount, Actual.AutoMount);
            Assert.AreEqual(Expected.AutoStart, Actual.AutoStart);
            Assert.AreEqual(Expected.DriveLetter, Actual.DriveLetter);
            Assert.AreEqual(Expected.ServerURL, Actual.ServerURL);
            Assert.AreEqual(0, Actual.ServerType, "AOS is no longer offered: every loaded configuration uses WebDAV");
            Assert.AreEqual(Expected.ServerLogin, Actual.ServerLogin);
            Assert.AreEqual(Expected.ServerPassword, Actual.ServerPassword);
            Assert.AreEqual(Expected.KernelCacheMode, Actual.KernelCacheMode);
            Assert.AreEqual(1, Actual.FlushMode, "FlushAtWrite is no longer offered: every loaded configuration uses FlushAtCleanup");
            Assert.AreEqual(Expected.SyncOps, Actual.SyncOps);
            Assert.AreEqual(Expected.PreLoading, Actual.PreLoading);
            Assert.AreEqual(Expected.MountAsNetworkDrive, Actual.MountAsNetworkDrive);
            Assert.AreEqual(Expected.HTTPProxyMode, Actual.HTTPProxyMode);
            Assert.AreEqual(Expected.ProxyURL, Actual.ProxyURL);
            Assert.AreEqual(Expected.UseProxyAuthentication, Actual.UseProxyAuthentication);
            Assert.AreEqual(Expected.ProxyLogin, Actual.ProxyLogin);
            Assert.AreEqual(Expected.ProxyPassword, Actual.ProxyPassword);
            Assert.AreEqual(Expected.UseClientCertForAuthentication, Actual.UseClientCertForAuthentication);
            Assert.AreEqual(Expected.CertStoreName, Actual.CertStoreName);
            Assert.AreEqual(Expected.CertStoreLocation, Actual.CertStoreLocation);
            Assert.AreEqual(Expected.CertSerial, Actual.CertSerial);
        }

        private static void AssertNotConfigured(Configuration C, string ExpectedPath)
        {
            Assert.IsNotNull(C);
            Assert.IsFalse(C.IsConfigured);
            Assert.AreEqual((short)0, C.HTTPProxyMode);
            Assert.AreEqual(ExpectedPath, C.Path);
        }

        #endregion
    }
}
