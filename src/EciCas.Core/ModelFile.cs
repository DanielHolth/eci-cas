namespace EciCas.Core;

/// <summary>
/// Where a weights file configured by a relative path actually is.
///
/// Every local model in this system is downloaded rather than committed --
/// embeddings, the speech model, the GGUF the local tier serves -- so they all
/// live outside bin/ and they are all named by a path relative to the repo
/// root. Resolving that against <see cref="AppContext.BaseDirectory"/> alone is
/// wrong in the ordinary case, because nothing copies hundreds of megabytes
/// into every build configuration and nobody wants a build that does.
/// </summary>
public static class ModelFile
{
    /// <summary>
    /// A relative weights path, resolved against the binary and then against
    /// each directory above it until the file turns up.
    ///
    /// This was learnt the expensive way once already: the configured
    /// embedding path pointed at a file that had never existed, the provider
    /// warned once, and every vector path in the system went quietly off --
    /// pair sweeps, row narrowing and Hindsight's wake alike -- while the
    /// weights sat four directories up.
    ///
    /// Walking up costs a few File.Exists calls once at startup and makes the
    /// same configured path work from `dotnet run`, from bin, and from a
    /// published layout, where the weights sit beside the binary and the first
    /// probe hits.
    ///
    /// The working directory is walked too, because a relative path typed at a
    /// prompt means what it means in the shell that typed it. A build whose
    /// output lives somewhere else entirely -- an artifacts path, a temp
    /// directory -- shares no ancestor with the repo, so the binary's own chain
    /// never reaches the weights however far up it climbs.
    ///
    /// A path that finds nothing comes back as the binary-relative reading, so
    /// whoever reports the absence names the path an operator would expect.
    /// </summary>
    public static string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, path);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return Path.Combine(AppContext.BaseDirectory, path);
    }
}
