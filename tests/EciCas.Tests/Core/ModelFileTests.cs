using EciCas.Core;

namespace EciCas.Tests.Core;

public class ModelFileTests
{
    [Fact]
    public void Resolve_WithAnAbsolutePath_LeavesItAlone()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "ggml-base.bin");

        Assert.Equal(rooted, ModelFile.Resolve(rooted));
    }

    /// <summary>
    /// The reason this walk exists. Weights live in the repository, once, and
    /// the binary lives several directories below it under bin/, so the path a
    /// human writes in configuration is relative to neither.
    /// </summary>
    [Fact]
    public void Resolve_WithAPathAboveTheBinary_FindsIt()
    {
        var root = Directory.CreateTempSubdirectory().FullName;

        // A name nothing else can own. The binary's own directory is tried
        // before the working one, and that walk reaches the repository -- where
        // a real model is exactly what a developer has sitting on disk.
        var relative = Path.Combine("models", "whisper", $"ggml-{Guid.NewGuid():N}.bin");
        var expected = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, string.Empty);

        var deep = Path.Combine(root, "src", "Shell", "bin", "Debug");
        Directory.CreateDirectory(deep);
        var restore = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(deep);
            Assert.Equal(expected, ModelFile.Resolve(relative));
        }
        finally
        {
            Directory.SetCurrentDirectory(restore);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A path, not null and not a throw. Nothing is missing until something
    /// tries to open it, and then the message names where it looked — which is
    /// the one thing the person needs in order to put the file there.
    /// </summary>
    [Fact]
    public void Resolve_WhenNothingIsThere_ReturnsThePathBesideTheBinary()
    {
        var absent = Path.Combine("models", "whisper", "ggml-nothing-is-here.bin");

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, absent), ModelFile.Resolve(absent));
    }
}
