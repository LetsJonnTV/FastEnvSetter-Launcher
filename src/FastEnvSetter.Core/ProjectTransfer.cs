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

    public static string Export(Project project, IReadOnlyList<EnvProfile> allProfiles, string rootFolder)
    {
        var profiles = EnvResolver.Layers(project, allProfiles).ToList();
        var package = Clone(new ProjectPackage { Project = project, Profiles = profiles });

        foreach (var variable in Variables(package))
        {
            variable.ProtectedValue = null;
            variable.DecryptionFailed = false;
            if (variable.IsSecret || variable.SecretRef is not null)
                variable.Value = "";
        }

        foreach (var service in package.Project.Services)
            service.WorkingDirectory = ToRelative(rootFolder, service.WorkingDirectory);

        return JsonSerializer.Serialize(package, Options);
    }

    /// <returns>Das Paket oder null, wenn die Datei kein gültiges Paket ist.</returns>
    public static ProjectPackage? Parse(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize<ProjectPackage>(json);
            return package is { Format: ProjectPackage.CurrentFormat } && package.Project.Services.Count > 0 ? package : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Gemeinsamer Elternordner aller Arbeitsordner.</summary>
    public static string CommonRoot(Project project)
    {
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

    /// <summary>Übernimmt ein Paket mit neuen IDs, damit es vorhandene Projekte nicht berührt.</summary>
    public static Project Import(ProjectPackage source, string rootFolder, AppData data)
    {
        var package = Clone(source);
        var ids = new Dictionary<Guid, Guid>();

        foreach (var profile in package.Profiles)
        {
            profile.Id = Remap(profile.Id);
            foreach (var variable in profile.Variables)
                variable.Id = Remap(variable.Id);
        }

        var project = package.Project;
        project.Id = Guid.NewGuid();
        foreach (var service in project.Services)
        {
            service.Id = Remap(service.Id);
            foreach (var variable in service.ExtraVariables)
                variable.Id = Remap(variable.Id);
        }

        foreach (var profile in package.Profiles)
        {
            profile.ParentId = profile.ParentId is { } parent ? Known(parent) : null;
            if (data.Profiles.Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                profile.Name += " (Import)";
        }

        project.EnvProfileIds = project.EnvProfileIds.Select(Known).OfType<Guid>().ToList();
        project.DisabledVariableIds = project.DisabledVariableIds.Select(Known).OfType<Guid>().ToHashSet();
        foreach (var service in project.Services)
        {
            service.DependsOn = service.DependsOn.Select(Known).OfType<Guid>().ToList();
            service.DisabledVariableIds = service.DisabledVariableIds.Select(Known).OfType<Guid>().ToHashSet();
            service.WorkingDirectory = service.WorkingDirectory.Length == 0 ? "" : Path.GetFullPath(Path.Combine(rootFolder, service.WorkingDirectory));
        }

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

    private static string ToRelative(string root, string folder)
    {
        if (folder.Length == 0 || root.Length == 0)
            return "";
        var relative = Path.GetRelativePath(root, folder);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? "" : relative;
    }

    private static IEnumerable<EnvVariable> Variables(ProjectPackage package) =>
        package.Profiles.SelectMany(p => p.Variables).Concat(package.Project.Services.SelectMany(s => s.ExtraVariables));

    private static ProjectPackage Clone(ProjectPackage package) =>
        JsonSerializer.Deserialize<ProjectPackage>(JsonSerializer.Serialize(package))!;
}
