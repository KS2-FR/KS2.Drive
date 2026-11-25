using KS2Drive.Config;
using KS2Drive.FS;
using KS2Drive.Log;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Tests of <see cref="FSPService"/> that never mount a drive letter: Mount is only called with configurations that fail
    /// before WinFSP is involved, and the event relaying is tested by attaching a DavFS to the service by reflection.
    /// </summary>
    [TestClass]
    public class FSPServiceTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [TestMethod]
        public void Constructor_CreatesAnUnmountedService()
        {
            var Service = new FSPService();
            Assert.IsNull(GetPrivateField(Service, "Host"));
            Assert.IsNull(GetPrivateField(Service, "davFs"));
        }

        [TestMethod]
        public void Unmount_WhenNothingIsMounted_DoesNotThrow()
        {
            var Service = new FSPService();
            Service.Unmount();
            Service.Unmount();
            Assert.IsNull(GetPrivateField(Service, "Host"));
            Assert.IsNull(GetPrivateField(Service, "davFs"));
        }

        [TestMethod]
        public void OnStop_WhenNothingIsMounted_DoesNotThrow()
        {
            var Service = new FSPService();
            InvokePrivate(Service, "OnStop");
            Assert.IsNull(GetPrivateField(Service, "Host"));
        }

        [TestMethod]
        public void Mount_InvalidServerUrl_ThrowsAndStaysUnmounted()
        {
            var Service = new FSPService();
            var Config = DavFsContext.TestConfiguration();
            Config.ServerURL = "not a valid url";

            var Ex = CatchException(() => Service.Mount(Config));

            Assert.IsNotNull(Ex, "Mount must fail");
            Assert.IsNull(GetPrivateField(Service, "Host"));
            Assert.IsNull(GetPrivateField(Service, "davFs"));
            Service.Unmount(); //Still safe to call
        }

        [TestMethod]
        public void Mount_ServerRejectsCredentials_ThrowsAndStaysUnmounted()
        {
            var Server = new FakeWebDavServer { RejectCredentials = true };
            var Service = new FSPService { TestHttpMessageHandler = Server };
            int AuthFailures = 0;
            Service.RepositoryAuthenticationFailed += (s, e) => AuthFailures++;

            var Ex = CatchException(() => Service.Mount(DavFsContext.TestConfiguration()));

            Assert.IsNotNull(Ex, "Mount must fail");
            StringAssert.Contains(Ex.Message, "Invalid credentials");
            Assert.IsNull(GetPrivateField(Service, "Host"));
            Assert.IsNull(GetPrivateField(Service, "davFs"));
            Assert.IsTrue(Server.Requests.Count > 0, "the connection is tested before mounting");
            Assert.AreEqual(0, AuthFailures);
        }

        [TestMethod]
        public void Mount_UnreachableServer_ThrowsAndStaysUnmounted()
        {
            var Server = new FakeWebDavServer { NetworkFailure = (m, p) => true };
            var Service = new FSPService { TestHttpMessageHandler = Server };

            var Ex = CatchException(() => Service.Mount(DavFsContext.TestConfiguration()));

            Assert.IsNotNull(Ex, "Mount must fail");
            StringAssert.Contains(Ex.Message, "Cannot connect to server");
            Assert.IsNull(GetPrivateField(Service, "Host"));
            Assert.IsNull(GetPrivateField(Service, "davFs"));
        }

        [TestMethod]
        public void RepositoryActionPerformed_IsRelayedFromDavFS()
        {
            using (var Ctx = DavFsContext.Create())
            {
                var Service = new FSPService();
                AttachLikeMount(Service, Ctx.Fs);
                var Received = new List<Tuple<object, LogListItem>>();
                Service.RepositoryActionPerformed += (s, e) => Received.Add(new Tuple<object, LogListItem>(s, e));
                var Item = new LogListItem { Method = "Test", File = "\\a.txt", Result = "OK" };

                RaiseDavFsEvent(Ctx.Fs, "RepositoryActionPerformed", Item);

                Assert.AreEqual(1, Received.Count);
                Assert.AreSame(Ctx.Fs, Received[0].Item1, "the original sender is kept");
                Assert.AreSame(Item, Received[0].Item2);
            }
        }

        [TestMethod]
        public void RepositoryAuthenticationFailed_IsRelayedFromDavFS()
        {
            using (var Ctx = DavFsContext.Create())
            {
                var Service = new FSPService();
                AttachLikeMount(Service, Ctx.Fs);
                object Sender = null;
                EventArgs Args = null;
                int Count = 0;
                Service.RepositoryAuthenticationFailed += (s, e) => { Count++; Sender = s; Args = e; };

                RaiseDavFsEvent(Ctx.Fs, "RepositoryAuthenticationFailed", EventArgs.Empty);

                Assert.AreEqual(1, Count);
                Assert.AreSame(Ctx.Fs, Sender);
                Assert.AreSame(EventArgs.Empty, Args);
            }
        }

        [TestMethod]
        public void RelayHandlers_WithoutSubscribers_DoNotThrow()
        {
            var Service = new FSPService();
            InvokePrivate(Service, "DavFs_RepositoryActionPerformed", null, new LogListItem());
            InvokePrivate(Service, "DavFs_RepositoryAuthenticationFailed", null, EventArgs.Empty);
        }

        [TestMethod]
        public void Unmount_DetachesTheFileSystemEvents()
        {
            using (var Ctx = DavFsContext.Create())
            {
                var Service = new FSPService();
                AttachLikeMount(Service, Ctx.Fs);
                int Actions = 0, AuthFailures = 0;
                Service.RepositoryActionPerformed += (s, e) => Actions++;
                Service.RepositoryAuthenticationFailed += (s, e) => AuthFailures++;

                Service.Unmount();

                Assert.IsNull(GetPrivateField(Service, "davFs"), "the DavFS is released");
                RaiseDavFsEvent(Ctx.Fs, "RepositoryActionPerformed", new LogListItem());
                RaiseDavFsEvent(Ctx.Fs, "RepositoryAuthenticationFailed", EventArgs.Empty);
                Assert.AreEqual(0, Actions, "events of the released DavFS are no longer relayed");
                Assert.AreEqual(0, AuthFailures);
                Assert.AreEqual(1, Ctx.Log.Count, "the DavFS itself still raises its events");
                Assert.AreEqual(1, Ctx.AuthenticationFailures);
            }
        }

        [TestMethod]
        public void DetachFileSystem_WhenNothingAttached_DoesNothing()
        {
            var Service = new FSPService();
            InvokePrivate(Service, "DetachFileSystem");
            Assert.IsNull(GetPrivateField(Service, "davFs"));
        }

        #region Private methods

        private static object GetPrivateField(FSPService Service, String Name) => typeof(FSPService).GetField(Name, PrivateInstance).GetValue(Service);

        private static void SetPrivateField(FSPService Service, String Name, object Value) => typeof(FSPService).GetField(Name, PrivateInstance).SetValue(Service, Value);

        private static void InvokePrivate(FSPService Service, String Name, params object[] Args)
        {
            try
            {
                typeof(FSPService).GetMethod(Name, PrivateInstance).Invoke(Service, Args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        /// <summary>Attaches the DavFS to the service the way Mount does, without creating the WinFSP host.</summary>
        private static void AttachLikeMount(FSPService Service, DavFS Fs)
        {
            SetPrivateField(Service, "davFs", Fs);
            Fs.RepositoryActionPerformed += (EventHandler<LogListItem>)Delegate.CreateDelegate(typeof(EventHandler<LogListItem>), Service, typeof(FSPService).GetMethod("DavFs_RepositoryActionPerformed", PrivateInstance));
            Fs.RepositoryAuthenticationFailed += (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), Service, typeof(FSPService).GetMethod("DavFs_RepositoryAuthenticationFailed", PrivateInstance));
        }

        /// <summary>Raises a DavFS event through its backing delegate field.</summary>
        private static void RaiseDavFsEvent(DavFS Fs, String EventName, object Args)
        {
            var Handler = (Delegate)typeof(DavFS).GetField(EventName, PrivateInstance).GetValue(Fs);
            Handler?.DynamicInvoke(Fs, Args);
        }

        private static Exception CatchException(Action Action)
        {
            try
            {
                Action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        #endregion
    }
}
