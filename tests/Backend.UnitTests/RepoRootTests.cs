using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class RepoRootTests : IDisposable
{
    private readonly string _rootDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "lookout-repo-root-" + Guid.NewGuid())).FullName;

    public void Dispose() => Directory.Delete(_rootDir, recursive: true);

    [TestMethod]
    public void FindsTheDirectoryWithTheSolutionFile()
    {
        File.WriteAllText(Path.Combine(_rootDir, RepoRoot.SolutionFileName), "");
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootDir, "src", "Backend")).FullName;

        Assert.AreEqual(_rootDir, RepoRoot.Find(projectDir));
        Assert.AreEqual(_rootDir, RepoRoot.Find(_rootDir));
    }

    [TestMethod]
    public void FindsNothingOutsideTheRepository()
    {
        Assert.IsNull(RepoRoot.Find(_rootDir));
        Assert.ThrowsExactly<InvalidOperationException>(() => RepoRoot.FindRequired(_rootDir));
    }
}
