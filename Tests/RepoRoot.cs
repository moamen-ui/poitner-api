public static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pointer.sln")))
                return dir.FullName;
            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"Could not locate Pointer.sln in any parent of {AppContext.BaseDirectory}"
        );
    }
}
