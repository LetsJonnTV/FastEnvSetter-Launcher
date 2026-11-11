namespace FastEnvSetter.Core;

public sealed record EnvFileOrigin(string Name, string Source, bool IsExample, bool IsSecret);
public sealed record EnvFilePreview(IReadOnlyList<EnvFileOrigin> Variables, IReadOnlyList<int> SkippedLines);

public static class EnvFileSources
{
    public const int MaximumFileBytes = 1024 * 1024;

    public static bool IsExample(string path)
    {
        var name = Path.GetFileName(path).TrimEnd(' ', '.');
        return name.Equals(".env-example", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-example", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
            || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':'))
            throw new ArgumentException("A source root and a relative project path are required.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.Equals(fullRoot, comparison)
            && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The path must remain inside the project source root.");
        for (var current = new DirectoryInfo(fullRoot); current is not null; current = current.Parent)
            RejectLink(current.FullName);
        var walk = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, fullPath).Split(Path.DirectorySeparatorChar))
        {
            walk = Path.Combine(walk, segment);
            RejectLink(walk);
        }
        return fullPath;
    }

    public static EnvFilePreview Preview(string sourceRoot, string relativePath)
    {
        var document = Read(sourceRoot, relativePath);
        return new EnvFilePreview(document.Entries.Select(entry =>
            new EnvFileOrigin(entry.Name, relativePath, IsExample(relativePath), DotEnv.LooksSecret(entry.Name))).ToArray(), document.SkippedLines);
    }

    public static ResolvedEnvironment Load(Project project)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new List<string>();
        var errors = new List<string>();
        foreach (var source in project.EnvironmentFiles)
        {
            try
            {
                if (IsExample(source))
                    throw new ArgumentException("Example files are preview-only.");
                var document = Read(project.SourceRoot, source);
                if (document.SkippedLines.Count > 0 || document.Entries.Any(entry => entry.Value.Contains('\0')))
                    throw new InvalidDataException();
                foreach (var entry in document.Entries)
                {
                    variables[entry.Name] = entry.Value;
                }
                secrets.AddRange(document.Entries.Select(entry => entry.Value).Where(value => value.Length > 0));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add("An environment source could not be loaded. Check its relative path, permissions and dotenv syntax; example files are preview-only.");
            }
        }
        return new ResolvedEnvironment(variables, secrets.Distinct().ToArray(), errors);
    }

    private static DotEnvDocument Read(string root, string source)
    {
        var path = ResolvePath(root, source);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("The environment source exceeds the size limit.");
        using var reader = new StreamReader(stream);
        return DotEnv.Parse(reader.ReadToEnd());
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Linked project paths are not supported.");
    }
}
