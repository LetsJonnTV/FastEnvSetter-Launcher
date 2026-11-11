using System.Text.Json;

namespace FastEnvSetter.Core;

public sealed class ProjectPackage
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public Project Project { get; set; } = new();
    public List<EnvProfile> Profiles { get; set; } = [];
}

/// <summary>Teilt Projekte als Datei. Lokale Secrets verlassen den Rechner dabei nie.</summary>
public static class ProjectTransfer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ResolvePath(string rootFolder, string path)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Path.IsPathFullyQualified(rootFolder))
            throw new ArgumentException("Ein absoluter Wurzelordner ist erforderlich.", nameof(rootFolder));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, string.IsNullOrWhiteSpace(path) ? "." : path)));
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Der Pfad muss innerhalb des gewählten Wurzelordners liegen.", nameof(path));
        var current = full;
        while (true)
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Verknüpfungen sind als Projektpfad nicht zulässig.", nameof(path));
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current)!;
        }
        return full;
    }

    public static string Export(Project project, IReadOnlyList<EnvProfile> allProfiles, string rootFolder)
        => Export(project, allProfiles, rootFolder, includeSecretReferences: true);

    public static string Export(Project project, IReadOnlyList<EnvProfile> allProfiles, string rootFolder, bool includeSecretReferences)
    {
        _ = ResolvePath(rootFolder, ".");
        var profiles = EnvResolver.Layers(project, allProfiles).ToList();
        var package = Clone(new ProjectPackage { Project = project, Profiles = profiles });

        foreach (var variable in Variables(package))
        {
            variable.ProtectedValue = null;
            variable.DecryptionFailed = false;
            if (variable.IsSecret || variable.SecretRef is not null)
                variable.Value = "";
            if (!includeSecretReferences && variable.SecretRef is not null)
            {
                variable.IsSecret = true;
                variable.SecretRef = null;
            }
        }

        foreach (var service in package.Project.Services)
            service.WorkingDirectory = ToRelative(rootFolder, service.WorkingDirectory);

        var sourceRoot = string.IsNullOrWhiteSpace(project.SourceRoot) ? rootFolder : project.SourceRoot;
        package.Project.SourceRoot = ToRelative(rootFolder, sourceRoot);
        MapDevelopmentPaths(package.Project, sourceRoot);

        return JsonSerializer.Serialize(package, Options);
    }

    /// <returns>Das Paket oder null, wenn die Datei kein gültiges Paket ist.</returns>
    public static ProjectPackage? Parse(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize<ProjectPackage>(json);
            if (package is not { Format: ProjectPackage.CurrentFormat })
                return null;
            if (package.Project is null || package.Project.Services is null || package.Project.EditorTargets is null || package.Project.Tasks is null
                || package.Project.EnvironmentFiles is null || package.Project.DeploymentTargets is null || package.Project.Releases is null
                || package.Profiles is null)
                return null;
            if (!HasValidMembers(package))
                return null;
            return package.Project.Services.Count > 0 || package.Project.EditorTargets.Count > 0
                || !string.IsNullOrWhiteSpace(package.Project.SourceRoot) ? package : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Gemeinsamer Elternordner aller Arbeitsordner.</summary>
    public static string CommonRoot(Project project)
    {
        if (!string.IsNullOrWhiteSpace(project.SourceRoot))
            return project.SourceRoot;
        var folders = project.Services.Select(s => s.WorkingDirectory).Where(f => f.Length > 0).ToList();
        if (folders.Count == 0)
            return "";

        var parts = folders[0].TrimEnd('\\', '/').Split('\\', '/').ToList();
        foreach (var other in folders.Skip(1).Select(f => f.TrimEnd('\\', '/').Split('\\', '/')))
        {
            var shared = parts.Zip(other).TakeWhile(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)).Count();
            parts = parts.Take(shared).ToList();
        }

        return parts.Count == 0 ? "" : string.Join('\\', parts) + (parts.Count == 1 ? "\\" : "");
    }

    /// <summary>Was das Paket enthält, für die Prüfung vor Export und Import.</summary>
    public static IReadOnlyList<string> Summarize(ProjectPackage package)
    {
        var variables = Variables(package).ToList();
        var lines = new List<string>
        {
            $"Projekt: {package.Project.Name}",
            $"Dienste: {string.Join(", ", package.Project.Services.Select(s => s.Name))}",
        };

        var plain = variables.Where(v => !v.IsSecret && v.SecretRef is null).Select(v => v.Name).ToList();
        var empty = variables.Where(v => v.IsSecret && v.SecretRef is null).Select(v => v.Name).ToList();
        var vault = variables.Where(v => v.SecretRef is not null).Select(v => v.Name).ToList();
        if (plain.Count > 0)
            lines.Add($"Variablen mit Wert: {string.Join(", ", plain)}");
        if (empty.Count > 0)
            lines.Add($"Geheim, ohne Wert: {string.Join(", ", empty)}");
        if (vault.Count > 0)
            lines.Add($"Aus Tresor: {string.Join(", ", vault)}");

        var commands = package.Project.Services
            .SelectMany(s => s.PreSteps.Concat(s.Settings.TryGetValue("customCommand", out var c) && c.Length > 0 ? [c] : Array.Empty<string>()))
            .ToList();
        if (commands.Count > 0)
            lines.Add($"Ausgeführte Befehle: {string.Join(" · ", commands)}");

        return lines;
    }

    private static bool HasValidMembers(ProjectPackage package)
    {
        var project = package.Project;
        return project.SourceRoot is not null && project.EnvProfileIds is not null && project.DisabledVariableIds is not null
            && project.Services.All(service => service is not null && service.WorkingDirectory is not null
                && service.DependsOn is not null && service.ExtraVariables is not null && service.Settings is not null
                && service.PreSteps is not null && service.DisabledVariableIds is not null)
            && project.EditorTargets.All(editor => editor is not null && editor.Path is not null && editor.Profile is not null)
            && project.Tasks.All(task => task is not null && task.WorkingDirectory is not null && task.FileName is not null && task.DependsOn is not null)
            && project.DeploymentTargets.All(target => target is not null && target.WorkingDirectory is not null && target.FilePath is not null)
            && project.Releases.All(release => release is not null)
            && project.EnvironmentFiles.All(path => path is not null)
            && package.Profiles.All(profile => profile is not null && profile.Name is not null && profile.Variables is not null)
            && Variables(package).All(variable => variable is not null);
    }

    /// <summary>Übernimmt ein Paket mit neuen IDs, damit es vorhandene Projekte nicht berührt.</summary>
    public static Project Import(ProjectPackage source, string rootFolder, AppData data)
    {
        var package = Clone(source);
        ValidatePackage(package);
        ValidateDependencies(package.Project);
        var ids = new Dictionary<Guid, Guid>();

        foreach (var profile in package.Profiles)
        {
            profile.Id = Remap(profile.Id);
            foreach (var variable in profile.Variables)
                variable.Id = Remap(variable.Id);
        }

        var project = package.Project;
        project.Name = AllocateName(project.Name, data.Projects.Select(existing => existing.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        project.Id = Guid.NewGuid();
        RemapDevelopmentIds(project);
        foreach (var service in project.Services)
        {
            service.Id = Remap(service.Id);
            foreach (var variable in service.ExtraVariables)
                variable.Id = Remap(variable.Id);
        }

        RebindProfiles(package, data, ids);

        project.EnvProfileIds = project.EnvProfileIds.Select(Known).OfType<Guid>().ToList();
        project.DisabledVariableIds = project.DisabledVariableIds.Select(Known).OfType<Guid>().ToHashSet();
        foreach (var service in project.Services)
        {
            service.DependsOn = service.DependsOn.Select(Known).OfType<Guid>().ToList();
            service.DisabledVariableIds = service.DisabledVariableIds.Select(Known).OfType<Guid>().ToHashSet();
            service.WorkingDirectory = service.WorkingDirectory.Length == 0 ? "" : ResolvePath(rootFolder, service.WorkingDirectory);
        }

        project.SourceRoot = ResolvePath(rootFolder, project.SourceRoot);
        MapDevelopmentPaths(project, project.SourceRoot);
        ClearImportedSecrets(package);

        data.Profiles.AddRange(package.Profiles);
        data.Projects.Add(project);
        return project;

        Guid Remap(Guid id)
        {
            var mapped = Guid.NewGuid();
            ids[id] = mapped;
            return mapped;
        }

        Guid? Known(Guid id) => ids.TryGetValue(id, out var mapped) ? mapped : null;
    }

    private static void RebindProfiles(ProjectPackage package, AppData data, IReadOnlyDictionary<Guid, Guid> ids)
    {
        var names = data.Profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in package.Profiles)
        {
            profile.ParentId = profile.ParentId is { } parent && ids.TryGetValue(parent, out var mapped) ? mapped : null;
            profile.Name = AllocateName(profile.Name, names);
        }
    }

    private static string AllocateName(string name, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Import";
        if (name.Length > 200 || name != name.Trim() || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid imported name.");
        if (names.Add(name)) return name;
        var attempts = names.Count + 1;
        for (var index = 1; index <= attempts; index++)
        {
            var suffix = index == 1 ? " (Import)" : $" (Import {index})";
            var candidate = name[..Math.Min(name.Length, 200 - suffix.Length)].TrimEnd() + suffix;
            if (names.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("Unable to allocate an imported name.");
    }

    private static void ValidatePackage(ProjectPackage package)
    {
        if (package.Format != ProjectPackage.CurrentFormat || !HasValidMembers(package))
            throw new InvalidDataException("Invalid project package.");
        var project = package.Project;
        var identities = new HashSet<Guid>();
        var allIds = package.Profiles.Select(profile => profile.Id).Append(project.Id)
            .Concat(Variables(package).Select(variable => variable.Id))
            .Concat(project.Services.Select(service => service.Id))
            .Concat(project.Tasks.Select(task => task.Id))
            .Concat(project.EditorTargets.Select(editor => editor.Id))
            .Concat(project.DeploymentTargets.Select(target => target.Id))
            .Concat(project.Releases.Select(release => release.Id));
        if (allIds.Any(id => id == Guid.Empty || !identities.Add(id)))
            throw new InvalidDataException("Imported identities must be unique and nonempty.");
        var profiles = package.Profiles.ToDictionary(profile => profile.Id);
        var variables = package.Profiles.SelectMany(profile => profile.Variables).Select(variable => variable.Id).ToHashSet();
        if (project.EnvProfileIds.Distinct().Count() != project.EnvProfileIds.Count
            || project.EnvProfileIds.Any(id => !profiles.ContainsKey(id))
            || project.DisabledVariableIds.Any(id => !variables.Contains(id))
            || project.Services.Any(service => service.DisabledVariableIds.Any(id => !variables.Contains(id))
                || service.DependsOn.Distinct().Count() != service.DependsOn.Count)
            || project.Tasks.Any(task => task.DependsOn.Distinct().Count() != task.DependsOn.Count))
            throw new InvalidDataException("Invalid imported reference.");
        foreach (var profile in package.Profiles)
        {
            var path = new HashSet<Guid> { profile.Id };
            var parent = profile.ParentId;
            while (parent is { } parentId)
            {
                if (!path.Add(parentId) || !profiles.TryGetValue(parentId, out var ancestor))
                    throw new InvalidDataException("Invalid imported profile ancestry.");
                parent = ancestor.ParentId;
            }
        }
    }

    private static void ClearImportedSecrets(ProjectPackage package)
    {
        foreach (var variable in Variables(package))
        {
            variable.ProtectedValue = null;
            variable.DecryptionFailed = false;
            if (variable.IsSecret || variable.SecretRef is not null)
                variable.Value = "";
            if (variable.SecretRef is not null)
                variable.IsSecret = true;
            variable.SecretRef = null;
        }
    }

    private static string ToRelative(string root, string folder)
    {
        if (folder.Length == 0 || root.Length == 0)
            return "";
        return Path.GetRelativePath(root, ResolvePath(root, folder));
    }

    public static Project Duplicate(Project source)
    {
        ValidateDependencies(source);
        var copy = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(source))!;
        copy.Id = Guid.NewGuid();
        copy.Name += " (Kopie)";
        var services = copy.Services.ToDictionary(service => service.Id, _ => Guid.NewGuid());
        var variables = copy.Services.SelectMany(service => service.ExtraVariables).ToDictionary(variable => variable.Id, _ => Guid.NewGuid());
        var originals = source.Services.SelectMany(service => service.ExtraVariables).ToDictionary(variable => variable.Id);
        foreach (var service in copy.Services)
        {
            service.Id = services[service.Id];
            service.DependsOn = service.DependsOn.Select(id => services[id]).ToList();
            service.DisabledVariableIds = service.DisabledVariableIds.Select(id => variables.GetValueOrDefault(id, id)).ToHashSet();
            foreach (var variable in service.ExtraVariables)
            {
                variable.DecryptionFailed = originals[variable.Id].DecryptionFailed;
                variable.Id = variables[variable.Id];
            }
        }
        copy.DisabledVariableIds = copy.DisabledVariableIds.Select(id => variables.GetValueOrDefault(id, id)).ToHashSet();
        RemapDevelopmentIds(copy);
        return copy;
    }

    private static void RemapDevelopmentIds(Project project)
    {
        foreach (var editor in project.EditorTargets)
            editor.Id = Guid.NewGuid();
        var tasks = project.Tasks.ToDictionary(task => task.Id, _ => Guid.NewGuid());
        foreach (var task in project.Tasks)
        {
            task.Id = tasks[task.Id];
            task.DependsOn = task.DependsOn.Select(id => tasks[id]).ToList();
        }
        foreach (var deployment in project.DeploymentTargets)
            deployment.Id = Guid.NewGuid();
        foreach (var release in project.Releases)
            release.Id = Guid.NewGuid();
    }

    private static void ValidateDependencies(Project project)
    {
        var services = project.Services.Select(service => service.Id).ToHashSet();
        var tasks = project.Tasks.Select(task => task.Id).ToHashSet();
        if (project.Services.Any(service => service.DependsOn.Any(id => !services.Contains(id)))
            || project.Tasks.Any(task => task.DependsOn.Any(id => !tasks.Contains(id))))
            throw new InvalidDataException("Eine Abhängigkeit verweist auf einen nicht vorhandenen Dienst oder Task.");
    }

    private static void MapDevelopmentPaths(Project project, string root)
    {
        foreach (var editor in project.EditorTargets)
            editor.Path = ToRelative(root, editor.Path);
        foreach (var task in project.Tasks)
        {
            var workingDirectory = ResolvePath(root, task.WorkingDirectory);
            task.WorkingDirectory = ToRelative(root, workingDirectory);
            if (Path.IsPathRooted(task.FileName) || task.FileName.Contains('/') || task.FileName.Contains('\\'))
                task.FileName = Path.GetRelativePath(workingDirectory, ResolvePath(root, Path.Combine(workingDirectory, task.FileName)));
        }
        project.EnvironmentFiles = project.EnvironmentFiles.Select(path => ToRelative(root, path)).ToList();
        foreach (var deployment in project.DeploymentTargets)
        {
            var workingDirectory = ResolvePath(root, deployment.WorkingDirectory);
            deployment.WorkingDirectory = ToRelative(root, workingDirectory);
            if (deployment.FilePath.Length > 0)
                deployment.FilePath = Path.GetRelativePath(workingDirectory, ResolvePath(root, Path.Combine(workingDirectory, deployment.FilePath)));
        }
    }

    private static IEnumerable<EnvVariable> Variables(ProjectPackage package) =>
        package.Profiles.SelectMany(p => p.Variables).Concat(package.Project.Services.SelectMany(s => s.ExtraVariables));

    private static ProjectPackage Clone(ProjectPackage package) =>
        JsonSerializer.Deserialize<ProjectPackage>(JsonSerializer.Serialize(package))!;
}
