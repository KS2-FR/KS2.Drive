using Fsp;
using KS2Drive.FS;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using static KS2Drive.Tests.FS.DavFSTestHelpers;

namespace KS2Drive.Tests.FS
{
    /// <summary>
    /// Rename of files and folders.
    /// </summary>
    [TestClass]
    public class DavFSRenameTests
    {
        private static DavFsContext CreateContext(WebDAVMode Mode = WebDAVMode.WebDAV, FlushMode Flush = FlushMode.FlushAtCleanup)
        {
            return DavFsContext.Create(s =>
            {
                s.AddFile("/dav/a.txt", "abc");
                s.AddFile("/dav/b.txt", "bbbb");
                s.AddFolder("/dav/folder");
                s.AddFile("/dav/folder/c.txt", "ccc");
                s.AddFolder("/dav/other");
            }, Flush, Mode);
        }

        [TestMethod]
        public void Rename_File_MovesItOnTheServerAndUpdatesTheNode()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/renamed.txt"));
                Assert.AreEqual(1, Ctx.Server.CountRequests("MOVE"));
                Assert.AreEqual("\\renamed.txt", Node.LocalPath);
                Assert.AreEqual("/dav/renamed.txt", Node.RepositoryPath);
                Assert.AreEqual("renamed.txt", Node.Name);
                var L = LastLog(Ctx, "Rename");
                Assert.AreEqual("STATUS_SUCCESS", L.Result);
                Assert.AreEqual("\\a.txt -> \\renamed.txt", L.File);

                Assert.AreSame(Node, Ctx.Open("\\renamed.txt"), "The cache key follows the rename");
            }
        }

        [TestMethod]
        public void Rename_FileToAnotherFolder_MovesIt()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\other\\a.txt", false));

                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/other/a.txt"));
                Assert.AreEqual("/dav/other/a.txt", Node.RepositoryPath);
            }
        }

        [TestMethod]
        public void Rename_ThenWrite_UploadsToTheNewPath()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false);

                Ctx.Write(Node, 3, "d");
                Ctx.CleanupAndClose(Node, "\\renamed.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("abcd", Ctx.Server.GetText("/dav/renamed.txt"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"), "The old name is not recreated");
            }
        }

        [TestMethod]
        public void Rename_WithPendingChanges_KeepsThemForTheNewPath()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Write(Node, 0, "X");

                Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false);
                Ctx.CleanupAndClose(Node, "\\renamed.txt", DavFsContext.ModifiedFlags);

                Assert.AreEqual("Xbc", Ctx.Server.GetText("/dav/renamed.txt"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Rename_SameName_DoesNothing()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                int RequestsBefore = Ctx.Server.Requests.Count;

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\a.txt", false));
                Assert.AreEqual(RequestsBefore, Ctx.Server.Requests.Count);
            }
        }

        [TestMethod]
        public void Rename_Folder_MovesItAndUpdatesTheCachedChildren()
        {
            using (var Ctx = CreateContext())
            {
                var Folder = Ctx.Open("\\folder");
                var Child = Ctx.Open("\\folder\\c.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Folder, null, "\\folder", "\\renamed", false));

                Assert.IsFalse(Ctx.Server.Exists("/dav/folder"));
                Assert.IsTrue(Ctx.Server.IsFolder("/dav/renamed"));
                Assert.AreEqual("ccc", Ctx.Server.GetText("/dav/renamed/c.txt"));
                Assert.AreEqual("\\renamed", Folder.LocalPath);
                Assert.AreEqual("/dav/renamed", Folder.RepositoryPath);
                Assert.AreEqual("\\renamed\\c.txt", Child.LocalPath);
                Assert.AreEqual("/dav/renamed/c.txt", Child.RepositoryPath);
                Assert.AreSame(Child, Ctx.Open("\\renamed\\c.txt"));
                Assert.AreSame(Folder, Ctx.Open("\\renamed"));

                Ctx.Write(Child, 3, "!");
                Ctx.CleanupAndClose(Child, "\\renamed\\c.txt", DavFsContext.ModifiedFlags);
                Assert.AreEqual("ccc!", Ctx.Server.GetText("/dav/renamed/c.txt"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/folder/c.txt"));
            }
        }

        [TestMethod]
        public void Rename_ExistingTarget_WithoutReplace_ReturnsCollision()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", false));

                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
                Assert.AreEqual("bbbb", Ctx.Server.GetText("/dav/b.txt"));
                Assert.AreEqual("\\a.txt", Node.LocalPath);
            }
        }

        [TestMethod]
        public void Rename_ExistingTarget_WithReplace_InWebDAVMode_ReturnsCollision()
        {
            using (var Ctx = CreateContext(WebDAVMode.WebDAV))
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                Assert.AreEqual(0, Ctx.Server.CountRequests("DELETE"));
                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
                Assert.AreEqual("bbbb", Ctx.Server.GetText("/dav/b.txt"));
            }
        }

        [TestMethod]
        public void Rename_ExistingFolderTarget_WithReplace_InAOSMode_ReturnsCollision()
        {
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Folder = Ctx.Open("\\folder");

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Folder, null, "\\folder", "\\other", true));

                Assert.AreEqual(0, Ctx.Server.CountRequests("DELETE"));
                Assert.IsTrue(Ctx.Server.Exists("/dav/folder/c.txt"));
            }
        }

        [TestMethod]
        public void Rename_ExistingTarget_WithReplace_InAOSMode_DeletesTheTargetBeforeTheMove()
        {
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");
                var Target = Ctx.Open("\\b.txt");

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                int DeleteIndex = Ctx.Server.Requests.IndexOf("DELETE /dav/b.txt");
                int MoveIndex = Ctx.Server.Requests.IndexOf("MOVE /dav/a.txt");
                Assert.IsTrue(DeleteIndex >= 0, "The target is deleted");
                Assert.IsTrue(MoveIndex > DeleteIndex, "The MOVE is sent once the deletion is complete");
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/b.txt"));
                Assert.IsFalse(Ctx.Server.Exists("/dav/a.txt"));
                Assert.AreSame(Node, Ctx.Open("\\b.txt"), "The cache now maps the target name to the renamed node");
            }
        }

        [TestMethod]
        public void Rename_WithReplace_InAOSMode_TargetDeleteAnswering404_IsAccepted()
        {
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");
                var Server = Ctx.Server;
                //The target is deleted by another client between the existence check and our DELETE, which then answers 404
                Server.ForcedStatus = (M, P) => { if (M == "DELETE" && P == "/dav/b.txt") { Server.Remove("/dav/b.txt"); return HttpStatusCode.NotFound; } return null; };

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                Assert.AreEqual(1, Ctx.Server.CountRequests("MOVE"));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/b.txt"));
            }
        }

        [TestMethod]
        public void Rename_WithReplace_InAOSMode_TargetDeleteAnswering404ButTargetStillThere_IsACollisionAndNothingIsOverwritten()
        {
            //Inconsistent server: the MOVE is sent with "Overwrite: F", so the existing target is never replaced silently
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");
                String TargetBefore = Ctx.Server.GetText("/dav/b.txt");
                Ctx.Server.ForcedStatus = Answer("DELETE", "/dav/b.txt", 404);

                Assert.AreEqual(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                Assert.AreEqual(TargetBefore, Ctx.Server.GetText("/dav/b.txt"));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [TestMethod]
        public void Rename_WithReplace_InAOSMode_TargetDeleteFailure_ReturnsCannotMake()
        {
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = Answer("DELETE", "/dav/b.txt", 500);

                Assert.AreEqual(FileSystemBase.STATUS_CANNOT_MAKE, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\b.txt", true));

                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/a.txt"));
                Assert.AreEqual("STATUS_CANNOT_MAKE", LastLog(Ctx, "Rename").Result);
            }
        }

        [TestMethod]
        public void Rename_NetworkFailureWhileCheckingTheTarget_ReturnsNetworkUnreachable()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.NetworkFailure = NetworkDown("PROPFIND");

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false));

                Assert.AreEqual("STATUS_NETWORK_UNREACHABLE", LastLog(Ctx, "Rename").Result);
                Assert.AreEqual(0, Ctx.Server.CountRequests("MOVE"));
            }
        }

        [TestMethod]
        public void Rename_ServerErrorWhileCheckingTheTarget_ReturnsCannotMake()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = Answer("PROPFIND", "/dav/renamed.txt", 500);

                Assert.AreEqual(FileSystemBase.STATUS_CANNOT_MAKE, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false));
                Assert.AreEqual(0, Ctx.AuthenticationFailures);
            }
        }

        [DataTestMethod]
        [DataRow("\\a.txt", "\\renamed.txt", "/dav/a.txt")]
        [DataRow("\\folder", "\\renamed", "/dav/folder")]
        public void Rename_MoveRefused_ReturnsAccessDeniedAndKeepsTheNode(String FileName, String NewFileName, String RepositoryPath)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open(FileName);
                Ctx.Server.ForcedStatus = AnswerAll("MOVE", 403);

                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.Fs.Rename(Node, null, FileName, NewFileName, false));

                Assert.AreEqual("STATUS_ACCESS_DENIED", LastLog(Ctx, "Rename").Result);
                Assert.AreEqual(FileName, Node.LocalPath);
                Assert.AreEqual(RepositoryPath, Node.RepositoryPath);
                Assert.IsTrue(Ctx.Server.Exists(RepositoryPath));
                Assert.AreSame(Node, Ctx.Open(FileName));
            }
        }

        [TestMethod]
        public void Rename_ToAMissingFolder_ReturnsAccessDenied()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Assert.AreEqual(FileSystemBase.STATUS_ACCESS_DENIED, Ctx.Fs.Rename(Node, null, "\\a.txt", "\\missing\\a.txt", false));
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/a.txt"));
            }
        }

        [DataTestMethod]
        [DataRow("\\a.txt", "\\renamed.txt")]
        [DataRow("\\folder", "\\renamed")]
        public void Rename_NetworkFailureDuringTheMove_ReturnsNetworkUnreachable(String FileName, String NewFileName)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open(FileName);
                Ctx.Server.NetworkFailure = NetworkDown("MOVE");

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.Rename(Node, null, FileName, NewFileName, false));

                Assert.AreEqual("STATUS_NETWORK_UNREACHABLE", LastLog(Ctx, "Rename").Result);
                Assert.AreEqual(FileName, Node.LocalPath);
            }
        }

        [DataTestMethod]
        [DataRow("\\a.txt", "\\renamed.txt")]
        [DataRow("\\folder", "\\renamed")]
        public void Rename_401DuringTheMove_ReturnsNetworkUnreachableAndNotifies(String FileName, String NewFileName)
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open(FileName);
                Ctx.Server.ForcedStatus = AnswerAll("MOVE", 401);

                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Ctx.Fs.Rename(Node, null, FileName, NewFileName, false));

                Assert.AreEqual(1, Ctx.AuthenticationFailures);
                Assert.AreEqual(FileName, Node.LocalPath);
            }
        }

        [TestMethod]
        public void Rename_401WhileCheckingTheTarget_ReturnsNetworkUnreachableAndNotifies()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.RejectCredentials = true;

                Int32 Status = Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false);

                Assert.AreEqual(1, Ctx.AuthenticationFailures);
                Assert.AreEqual(FileSystemBase.STATUS_NETWORK_UNREACHABLE, Status, "401 is reported as STATUS_NETWORK_UNREACHABLE (documented behaviour)");
            }
        }

        [TestMethod]
        public void Rename_OfANodeNoLongerInTheCache_Succeeds()
        {
            using (var Ctx = CreateContext(WebDAVMode.WebDAV, FlushMode.FlushAtWrite))
            {
                var Node = Ctx.Open("\\a.txt");
                Ctx.Server.ForcedStatus = AnswerAll("PUT", 500);
                Ctx.Write(Node, 0, "X"); //The failed write flush removes the node from the cache
                Ctx.Server.ForcedStatus = null;
                Assert.IsFalse(GetCachedNodes(Ctx.Fs).ContainsKey("\\a.txt"));

                Int32 Status = Ctx.Fs.Rename(Node, null, "\\a.txt", "\\renamed.txt", false);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual("\\renamed.txt", Node.LocalPath);
                Assert.IsTrue(Ctx.Server.Exists("/dav/renamed.txt"));
            }
        }

        [TestMethod]
        public void Rename_CaseOnly_RenamesTheFileWithoutCollision()
        {
            using (var Ctx = CreateContext())
            {
                var Node = Ctx.Open("\\a.txt");

                Int32 Status = Ctx.Fs.Rename(Node, null, "\\a.txt", "\\A.txt", false);

                Assert.AreEqual(FileSystemBase.STATUS_SUCCESS, Status);
                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/A.txt"));
                Assert.AreEqual("\\A.txt", Node.LocalPath);
            }
        }

        [TestMethod]
        public void Rename_CaseOnly_WithReplace_InAOSMode_NeverDeletesTheFileItself()
        {
            using (var Ctx = CreateContext(WebDAVMode.AOS))
            {
                var Node = Ctx.Open("\\a.txt");

                Ctx.Fs.Rename(Node, null, "\\a.txt", "\\A.txt", true);

                Assert.AreEqual("abc", Ctx.Server.GetText("/dav/A.txt"), "The file content must survive a case-only rename");
            }
        }
    }
}
