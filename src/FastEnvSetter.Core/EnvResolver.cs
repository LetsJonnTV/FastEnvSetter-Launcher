namespace FastEnvSetter.Core;

public sealed record ResolvedEnvironment(
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> SecretValues,
    IReadOnlyList<string> Errors);

/// <summary>Eine Variable mit ihrer Herkunft, für die Anzeige im Editor.</summary>
public sealed record EnvLayerRow(EnvVariable Variable, EnvProfile Profile, bool IsInherited, bool IsOverridden);

public static class EnvResolver
{
    /// <summary>Die Profile eines Projekts in Anwendungsreihenfolge, Basisprofile jeweils zuerst.</summary>
    public static IReadOnlyList<EnvProfile> Layers(Project project, IReadOnlyList<EnvProfile> allProfiles, ICollection<string>? errors = null)
    {
        var result = new List<EnvProfile>();
        foreach (var id in project.EnvProfileIds)
        {
            var chain = new List<EnvProfile>();
            var visited = new HashSet<Guid>();
            var current = allProfiles.FirstOrDefault(p => p.Id == id);
            while (current is not null)
            {
                if (!visited.Add(current.Id))
                {
                    errors?.Add($"Das Profil „{current.Name}“ erbt im Kreis.");
                    break;
                }

                chain.Insert(0, current);
                var parentId = current.ParentId;
                current = parentId is null ? null : allProfiles.FirstOrDefault(p => p.Id == parentId);
            }

            // Ein gemeinsames Basisprofil zählt nur einmal, damit es Spezialisierungen nicht überschreibt.
            result.AddRange(chain.Where(p => !result.Contains(p)));
        }

        return result;
    }

    public static IReadOnlyList<EnvLayerRow> Describe(Project project, IReadOnlyList<EnvProfile> allProfiles)
    {
        var layers = Layers(project, allProfiles);
        var selected = project.EnvProfileIds.ToHashSet();
        var rows = new List<EnvLayerRow>();
        for (var i = 0; i < layers.Count; i++)
        {
            var later = layers.Skip(i + 1)
                .SelectMany(l => l.Variables)
                .Where(v => !project.DisabledVariableIds.Contains(v.Id))
                .Select(v => v.Name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            rows.AddRange(layers[i].Variables.Select(v =>
                new EnvLayerRow(v, layers[i], !selected.Contains(layers[i].Id), later.Contains(v.Name.Trim()))));
        }

        return rows;
    }

    public static ResolvedEnvironment Resolve(Project project, ServiceDefinition? service, IReadOnlyList<EnvProfile> allProfiles)
    {
        var errors = new List<string>();
        var winners = new Dictionary<string, EnvVariable>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<Guid>(project.DisabledVariableIds);
        if (service is not null)
            disabled.UnionWith(service.DisabledVariableIds);

        foreach (var layer in Layers(project, allProfiles, errors))
            Apply(layer.Variables);
        if (service is not null)
            Apply(service.ExtraVariables);

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new List<string>();
        foreach (var (name, variable) in winners)
        {
            if (variable.IsSecret && variable.DecryptionFailed)
            {
                errors.Add($"„{name}“ lässt sich nicht entschlüsseln. Bitte den Wert neu eingeben.");
                continue;
            }

            variables[name] = variable.Value;
            if (variable.IsSecret && variable.Value.Length > 0)
                secrets.Add(variable.Value);
        }

        return new ResolvedEnvironment(variables, secrets, errors);

        void Apply(IEnumerable<EnvVariable> layerVariables)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in layerVariables.Where(v => !disabled.Contains(v.Id)))
            {
                var name = variable.Name.Trim();
                if (name.Length == 0)
                    errors.Add("Eine angehakte Variable hat keinen Namen.");
                else if (name.Contains('=') || name.Contains('\0'))
                    errors.Add($"Der Variablenname „{name}“ ist ungültig: „=“ ist nicht erlaubt.");
                else if (!seen.Add(name))
                    errors.Add($"Die Variable „{name}“ ist mehrfach angehakt.");
                else
                    winners[name] = variable;
            }
        }
    }
}
