using Fsp;
using KS2Drive.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KS2Drive.Tests
{
    [TestClass]
    public class SmokeTests
    {
        [TestMethod]
        public void FileSystem_ReadsAFileFromTheFakeServer()
        {
            using (var Ctx = DavFsContext.Create(s => s.AddFile("/dav/hello.txt", "Hello")))
            {
                var Node = Ctx.Open("\\hello.txt");
                Assert.AreEqual("Hello", Ctx.ReadAll(Node));
            }
        }
    }
}
