using ChasePie.Lookout.Runner.Services;

namespace ChasePie.Lookout.Runner.UnitTests;

[TestClass]
public sealed class RealPathTests
{
    [TestMethod]
    public void ResolvesLinksAnywhereInThePath()
    {
        var root = Directory.CreateTempSubdirectory("lookout-realpath-");
        try
        {
            var real = Directory.CreateDirectory(Path.Join(root.FullName, "real", "inner"));
            Directory.CreateSymbolicLink(Path.Join(root.FullName, "link"), Path.Join(root.FullName, "real"));

            var resolved = RealPath.Of(Path.Join(root.FullName, "link", "inner", "file-to-write.json"));

            Assert.AreEqual(Path.Join(RealPath.Of(real.FullName), "file-to-write.json"), resolved);
            Assert.DoesNotContain(Path.Join(root.FullName, "link"), resolved);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
