using System.Text.Json;
using System.Text.Json.Serialization;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class ConfigurationService(IConfigurationStore store, RunnerRegistry runners, IProcessSupervisor supervisor) : IConfigurationService
{
    public const int MaximumRequestLength = 131072;
    public const int MaximumResponseLength = 524288;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly HashSet<string> Reads = ["get_project", "list_projects", "get_profile", "list_profiles", "get_variable", "list_variables", "get_service", "list_services", "list_runners", "runner_schema", "validate", "preview_environment", "capabilities"];
    private static readonly HashSet<string> Writes = ["create_project", "update_project", "delete_project", "create_profile", "update_profile", "delete_profile", "create_variable", "update_variable", "delete_variable", "create_service", "update_service", "delete_service", "set_project_profiles", "set_variable_selection", "set_project_tasks", "set_project_env_files", "set_editor_targets", "batch"];

    public string ExecuteJson(string? json)
    {
        if (json is null || json.Length > MaximumRequestLength)
            return JsonSerializer.Serialize(Failure("invalid_request"), Json);
        try
        {
            var response = JsonSerializer.Serialize(Execute(JsonSerializer.Deserialize<ConfigurationRequest>(json, Json)), Json);
            return response.Length <= MaximumResponseLength ? response : JsonSerializer.Serialize(Failure("response_too_large"), Json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(Failure("invalid_request"), Json);
        }
    }

    public ConfigurationResponse Execute(ConfigurationRequest? request)
    {
        try
        {
            Require(request is not null && request.Version == 1 && request.Operation is not null, "invalid_request");
            var serialized = JsonSerializer.Serialize(request, Json);
            Require(serialized.Length <= MaximumRequestLength, "invalid_request");
            var input = JsonSerializer.Deserialize<ConfigurationRequest>(serialized, Json)!;
            if (Reads.Contains(input.Operation))
                return store.ReadConfiguration(data => new ConfigurationResponse(true, "ok", data.Revision, JsonSerializer.SerializeToElement(Read(data, input), Json)));
            Require(Writes.Contains(input.Operation), "unknown_operation");
            return store.CommitConfiguration(data =>
            {
                Require(data.Settings.AllowExternalConfigurationChanges, "permission_required");
                Require(input.RequestId is { } id && id != Guid.Empty && input.ExpectedRevision is >= 0, "invalid_request");
                var previous = data.ConfigurationReceipts.FirstOrDefault(receipt => receipt.RequestId == input.RequestId);
                if (previous is not null)
                    throw new Replay(previous);
                Require(data.Revision == input.ExpectedRevision, "revision_conflict");
                Require(data.ConfigurationReceipts.Count < 10000, "replay_capacity_reached");
                Guid? entityId;
                if (input.Operation == "batch")
                {
                    Require(input.Changes is { Count: > 0 and <= 100 }, "invalid_request");
                    entityId = null;
                    foreach (var change in input.Changes!)
                    {
                        Require(change is not null && change.Version == 1 && Writes.Contains(change.Operation) && change.Operation != "batch", "invalid_request");
                        entityId = Mutate(data, change!);
                    }
                }
                else
                    entityId = Mutate(data, input);
                Validate(data);
                data.Revision = checked(data.Revision + 1);
                data.ConfigurationReceipts.Add(new(input.RequestId!.Value, data.Revision, entityId));
                return new ConfigurationResponse(true, "ok", data.Revision, EntityId: entityId);
            });
        }
        catch (Replay replay)
        {
            return new(true, "replayed", replay.Receipt.Revision, EntityId: replay.Receipt.EntityId, Replayed: true);
        }
        catch (ConfigurationError error) { return Failure(error.Code); }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return Failure("invalid_request");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return Failure("persistence_failed");
        }
    }

    private ConfigurationResponse Failure(string code) => store.ReadConfiguration(data => new ConfigurationResponse(false, code, data.Revision));
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code = "validation_failed")
    {
        if (!condition) throw new ConfigurationError(code);
    }
    private sealed class ConfigurationError(string code) : Exception { public string Code { get; } = code; }
    private sealed class Replay(ConfigurationReceipt receipt) : Exception { public ConfigurationReceipt Receipt { get; } = receipt; }
    private static Project Project(AppData data, Guid? id) => data.Projects.FirstOrDefault(item => item.Id == id) ?? throw new ConfigurationError("not_found");
    private static EnvProfile Profile(AppData data, Guid? id) => data.Profiles.FirstOrDefault(item => item.Id == id) ?? throw new ConfigurationError("not_found");
    private static ServiceDefinition Service(Project project, Guid? id) => project.Services.FirstOrDefault(item => item.Id == id) ?? throw new ConfigurationError("not_found");
    private static List<EnvVariable> Variables(AppData data, ConfigurationRequest input)
    {
        Require(input.ServiceId is null
            ? input.ProfileId is not null && input.ProjectId is null
            : input.ProjectId is not null && input.ProfileId is null, "invalid_request");
        return input.ServiceId is null ? Profile(data, input.ProfileId).Variables : Service(Project(data, input.ProjectId), input.ServiceId).ExtraVariables;
    }
    private static EnvVariable Variable(AppData data, ConfigurationRequest input) => Variables(data, input).FirstOrDefault(item => item.Id == input.VariableId) ?? throw new ConfigurationError("not_found");
    private void Inactive(ServiceDefinition service) => Require(supervisor.GetState(service.Id) is ServiceState.Stopped or ServiceState.Failed or ServiceState.Blocked, "service_active");

    private Guid? Mutate(AppData data, ConfigurationRequest input)
    {
        switch (input.Operation)
        {
            case "create_project":
            case "update_project":
                {
                    var fields = input.Project ?? throw new ConfigurationError("invalid_request");
                    var project = input.Operation == "create_project" ? new Project() : Project(data, input.ProjectId);
                    foreach (var service in project.Services) Inactive(service);
                    project.Name = fields.Name;
                    project.SourceRoot = fields.SourceRoot;
                    project.IsFavorite = fields.IsFavorite;
                    project.Tags = fields.Tags ?? [];
                    if (input.Operation == "create_project") data.Projects.Add(project);
                    return project.Id;
                }
            case "delete_project":
                {
                    var project = Project(data, input.ProjectId);
                    foreach (var service in project.Services) Inactive(service);
                    data.Projects.Remove(project);
                    return project.Id;
                }
            case "create_profile":
            case "update_profile":
                {
                    var fields = input.Profile ?? throw new ConfigurationError("invalid_request");
                    var profile = input.Operation == "create_profile" ? new EnvProfile() : Profile(data, input.ProfileId);
                    profile.Name = fields.Name;
                    profile.ParentId = fields.ParentId;
                    if (input.Operation == "create_profile") data.Profiles.Add(profile);
                    return profile.Id;
                }
            case "delete_profile":
                {
                    var profile = Profile(data, input.ProfileId);
                    foreach (var child in data.Profiles.Where(item => item.ParentId == profile.Id)) child.ParentId = profile.ParentId;
                    foreach (var project in data.Projects) project.EnvProfileIds.RemoveAll(id => id == profile.Id);
                    foreach (var variable in profile.Variables) CleanupVariable(data, variable.Id);
                    data.Profiles.Remove(profile);
                    return profile.Id;
                }
            case "set_project_tasks":
                {
                    var project = Project(data, input.ProjectId);
                    Require(input.Tasks is not null && input.Tasks.All(task => task is not null), "invalid_request");
                    foreach (var service in project.Services) Inactive(service);
                    project.Tasks = input.Tasks!.Select(task => new ProjectTask
                    {
                        Id = task.Id,
                        Name = task.Name,
                        Kind = task.Kind,
                        FileName = task.FileName,
                        Arguments = task.Arguments,
                        WorkingDirectory = task.WorkingDirectory,
                        DependsOn = task.DependsOn ?? [],
                        TimeoutSeconds = task.TimeoutSeconds,
                    }).ToList();
                    return project.Id;
                }
            case "set_project_env_files":
                {
                    var project = Project(data, input.ProjectId);
                    Require(input.EnvironmentFiles is not null, "invalid_request");
                    foreach (var service in project.Services) Inactive(service);
                    project.EnvironmentFiles = input.EnvironmentFiles!;
                    return project.Id;
                }
            case "set_editor_targets":
                {
                    var project = Project(data, input.ProjectId);
                    Require(input.EditorTargets is not null && input.EditorTargets.All(target => target is not null), "invalid_request");
                    project.EditorTargets = input.EditorTargets!.Select(target => new EditorTarget
                    {
                        Id = target.Id,
                        Name = target.Name,
                        Path = target.Path,
                        Profile = target.Profile,
                    }).ToList();
                    return project.Id;
                }
            case "set_project_profiles":
                {
                    Require(input.ProfileIds is not null, "invalid_request");
                    var project = Project(data, input.ProjectId);
                    project.EnvProfileIds = input.ProfileIds!;
                    return project.Id;
                }
            case "set_variable_selection":
                {
                    Require(input.DisabledVariableIds is not null, "invalid_request");
                    var project = Project(data, input.ProjectId);
                    if (input.ServiceId is null) project.DisabledVariableIds = input.DisabledVariableIds!.ToHashSet();
                    else
                    {
                        var service = Service(project, input.ServiceId);
                        Inactive(service);
                        service.DisabledVariableIds = input.DisabledVariableIds!.ToHashSet();
                    }
                    return input.ServiceId ?? project.Id;
                }
            case "create_variable":
            case "update_variable":
                {
                    var fields = input.Variable ?? throw new ConfigurationError("invalid_request");
                    if (input.ServiceId is not null) Inactive(Service(Project(data, input.ProjectId), input.ServiceId));
                    var variable = input.Operation == "create_variable" ? new EnvVariable() : Variable(data, input);
                    Require(fields.Value is null || fields.Value.Length <= 32768, "invalid_request");
                    Require(fields.Value?.Contains('\0') != true);
                    Require(fields.SecretRef is null || fields.Value is null, "invalid_request");
                    if (fields.Value is null && fields.SecretRef is null && input.Operation == "update_variable")
                        Require(variable.SecretRef is null && variable.IsSecret == fields.IsSecret, "value_required");
                    variable.Name = fields.Name;
                    variable.IsSecret = fields.IsSecret || fields.SecretRef is not null;
                    variable.SecretRef = fields.SecretRef;
                    if (fields.Value is not null || fields.SecretRef is not null)
                    {
                        variable.Value = fields.SecretRef is null ? fields.Value! : "";
                        variable.ProtectedValue = null;
                        variable.DecryptionFailed = false;
                    }
                    if (input.Operation == "create_variable") Variables(data, input).Add(variable);
                    return variable.Id;
                }
            case "delete_variable":
                {
                    if (input.ServiceId is not null) Inactive(Service(Project(data, input.ProjectId), input.ServiceId));
                    var variable = Variable(data, input);
                    Variables(data, input).Remove(variable);
                    CleanupVariable(data, variable.Id);
                    return variable.Id;
                }
            case "create_service":
            case "update_service":
                {
                    var project = Project(data, input.ProjectId);
                    var fields = input.Service ?? throw new ConfigurationError("invalid_request");
                    var service = input.Operation == "create_service" ? new ServiceDefinition() : Service(project, input.ServiceId);
                    Inactive(service);
                    service.Name = fields.Name;
                    service.RunnerId = fields.RunnerId;
                    service.WorkingDirectory = fields.WorkingDirectory;
                    service.Settings = fields.Settings ?? [];
                    service.DependsOn = fields.DependsOn ?? [];
                    service.PreSteps = fields.PreSteps ?? [];
                    service.Readiness = fields.Readiness ?? new();
                    service.OutputMode = fields.OutputMode;
                    service.RestartPolicy = fields.RestartPolicy;
                    service.StartDelaySeconds = fields.StartDelaySeconds;
                    service.ExpectedPorts = fields.ExpectedPorts ?? [];
                    service.OpenUrl = fields.OpenUrl;
                    if (input.Operation == "create_service") project.Services.Add(service);
                    return service.Id;
                }
            case "delete_service":
                {
                    var project = Project(data, input.ProjectId);
                    var service = Service(project, input.ServiceId);
                    Inactive(service);
                    foreach (var dependent in project.Services.Where(item => item.DependsOn.Contains(service.Id)))
                    {
                        Inactive(dependent);
                        dependent.DependsOn.RemoveAll(id => id == service.Id);
                    }
                    foreach (var variable in service.ExtraVariables) CleanupVariable(data, variable.Id);
                    project.Services.Remove(service);
                    return service.Id;
                }
            default: throw new ConfigurationError("unknown_operation");
        }
    }

    private static void CleanupVariable(AppData data, Guid id)
    {
        foreach (var project in data.Projects)
        {
            project.DisabledVariableIds.Remove(id);
            foreach (var service in project.Services) service.DisabledVariableIds.Remove(id);
        }
    }

    private static object VariableView(EnvVariable variable) => new { variable.Id, variable.Name, secret = variable.IsSecret || variable.SecretRef is not null, source = variable.SecretRef is null ? "local" : "vault", variable.DecryptionFailed };
    private static object ProfileView(EnvProfile profile) => new { profile.Id, profile.Name, profile.ParentId, variables = profile.Variables.Select(VariableView).ToArray() };
    private static object ServiceView(ServiceDefinition service) => new { service.Id, service.Name, service.RunnerId, settingKeys = service.Settings.Keys.ToArray(), service.DependsOn, variables = service.ExtraVariables.Select(VariableView).ToArray() };
    private static object ProjectView(Project project) => new
    {
        project.Id,
        project.Name,
        project.SourceRoot,
        project.IsFavorite,
        project.Tags,
        project.EnvProfileIds,
        project.DisabledVariableIds,
        services = project.Services.Select(ServiceView).ToArray(),
        taskCount = project.Tasks.Count,
        tasks = project.Tasks.Select(task => new { task.Id, task.Name, task.Kind, argumentCount = task.Arguments.Count, task.DependsOn, task.TimeoutSeconds }).ToArray(),
        environmentFileCount = project.EnvironmentFiles.Count,
        environmentFiles = project.EnvironmentFiles.Select((path, index) => new { index, isExample = EnvFileSources.IsExample(path) }).ToArray(),
        editorTargetCount = project.EditorTargets.Count,
        editorTargets = project.EditorTargets.Select(target => new { target.Id, target.Name }).ToArray(),
    };

    private object Read(AppData data, ConfigurationRequest input)
    {
        switch (input.Operation)
        {
            case "capabilities": return new { version = 1, reads = Reads.ToArray(), writes = Writes.ToArray(), mutationsAllowed = data.Settings.AllowExternalConfigurationChanges, maximumRequestLength = MaximumRequestLength, maximumResponseLength = MaximumResponseLength, executionSensitiveWrites = true };
            case "list_projects": return data.Projects.Select(ProjectView).ToArray();
            case "get_project": return ProjectView(Project(data, input.ProjectId));
            case "list_profiles": return data.Profiles.Select(ProfileView).ToArray();
            case "get_profile": return ProfileView(Profile(data, input.ProfileId));
            case "list_variables": return Variables(data, input).Select(VariableView).ToArray();
            case "get_variable": return VariableView(Variable(data, input));
            case "list_services": return Project(data, input.ProjectId).Services.Select(ServiceView).ToArray();
            case "get_service": return ServiceView(Service(Project(data, input.ProjectId), input.ServiceId));
            case "list_runners": return runners.All.Select(runner => new { runner.Id, runner.DisplayName, runner.RequiresWorkingDirectory }).ToArray();
            case "runner_schema":
                var runner = runners.Find(input.RunnerId ?? "") ?? throw new ConfigurationError("not_found");
                return runner.GetSettingsSchema().Select(field => new { field.Key, field.Label, field.Kind, field.Required, field.Choices, field.AllowCustomValue, field.VisibleWhenKey, field.VisibleWhenValues }).ToArray();
            case "validate": Validate(data); return new { valid = true };
            case "preview_environment":
                {
                    var project = Project(data, input.ProjectId);
                    var service = input.ServiceId is null ? null : Service(project, input.ServiceId);
                    var winners = EnvResolver.Winners(project, service, data.Profiles);
                    return new
                    {
                        includesHostEnvironment = false,
                        fileSourcesEvaluated = false,
                        fileSourceCount = project.EnvironmentFiles.Count,
                        variables = winners.Values.Select(variable => new
                        {
                            variable.Id,
                            variable.Name,
                            secret = variable.IsSecret || variable.SecretRef is not null,
                            profileId = data.Profiles.FirstOrDefault(profile => profile.Variables.Any(item => item.Id == variable.Id))?.Id,
                            serviceId = service?.ExtraVariables.Any(item => item.Id == variable.Id) == true ? service.Id : (Guid?)null,
                        }).ToArray(),
                    };
                }
            default: throw new ConfigurationError("unknown_operation");
        }
    }

    private void Validate(AppData data)
    {
        static void Name(string name) => Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name == name.Trim() && !name.Any(char.IsControl));
        static void Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            foreach (var name in list) Name(name);
            Require(list.Distinct(StringComparer.OrdinalIgnoreCase).Count() == list.Count);
        }
        static void Acyclic(Guid id, Func<Guid, IEnumerable<Guid>> next, HashSet<Guid> path, HashSet<Guid> done)
        {
            if (done.Contains(id)) return;
            Require(path.Add(id), "cycle_detected");
            foreach (var child in next(id)) Acyclic(child, next, path, done);
            path.Remove(id);
            done.Add(id);
        }
        Names(data.Projects.Select(project => project.Name));
        Names(data.Profiles.Select(profile => profile.Name));
        var ids = new HashSet<Guid>();
        void Identity(Guid id) => Require(id != Guid.Empty && ids.Add(id));
        void CheckVariables(List<EnvVariable> variables)
        {
            Names(variables.Select(variable => variable.Name));
            foreach (var variable in variables)
            {
                Identity(variable.Id);
                Require(!variable.Name.Contains('=') && variable.Value is not null && !variable.Value.Contains('\0'));
                if (variable.SecretRef is { } reference)
                    Require(!string.IsNullOrWhiteSpace(reference.ProviderId) && !string.IsNullOrWhiteSpace(reference.Reference));
            }
        }
        var profileIds = data.Profiles.Select(profile => profile.Id).ToHashSet();
        var doneProfiles = new HashSet<Guid>();
        foreach (var profile in data.Profiles)
        {
            Identity(profile.Id);
            Require(profile.ParentId is null || profileIds.Contains(profile.ParentId.Value), "missing_reference");
            CheckVariables(profile.Variables);
        }
        foreach (var profile in data.Profiles)
            Acyclic(profile.Id, id => Profile(data, id).ParentId is { } parent ? [parent] : [], [], doneProfiles);
        var variableIds = data.Profiles.SelectMany(profile => profile.Variables).Select(variable => variable.Id).ToHashSet();
        foreach (var project in data.Projects)
        {
            Identity(project.Id);
            Require(project.SourceRoot is not null && (project.SourceRoot.Length == 0 || Path.IsPathFullyQualified(project.SourceRoot)));
            Require(project.Tags is not null && project.Tags.All(tag => tag is not null && tag.Length <= 200));
            Require(project.EnvProfileIds.Count == project.EnvProfileIds.Distinct().Count() && project.EnvProfileIds.All(profileIds.Contains), "missing_reference");
            Require(project.DisabledVariableIds.All(variableIds.Contains), "missing_reference");
            Require(project.Tasks is not null && project.Tasks.All(task => task is not null));
            Names(project.Tasks!.Select(task => task.Name));
            foreach (var task in project.Tasks)
            {
                Identity(task.Id);
                Require(Enum.IsDefined(task.Kind));
                Require(!string.IsNullOrWhiteSpace(task.FileName) && task.FileName.Length <= 4096 && !task.FileName.Any(char.IsControl));
                Require(task.Arguments is not null && task.Arguments.Count <= 1024
                    && task.Arguments.All(argument => argument is not null && argument.Length <= 32768 && !argument.Contains('\0')));
                Require(task.DependsOn is not null && task.DependsOn.Count == task.DependsOn.Distinct().Count());
                Require(task.TimeoutSeconds is >= 1 and <= 86400);
                Require(!string.IsNullOrWhiteSpace(task.WorkingDirectory) && task.WorkingDirectory.Length <= 4096 && !task.WorkingDirectory.Any(char.IsControl));
                EnvFileSources.ResolvePath(project.SourceRoot, task.WorkingDirectory);
                if (Path.IsPathRooted(task.FileName) || task.FileName.IndexOfAny(['/', '\\', ':']) >= 0)
                    EnvFileSources.ResolvePath(project.SourceRoot, task.FileName);
            }
            Require(WorkflowGraph.Plan(project.Tasks).Errors.Count == 0);
            Require(project.EnvironmentFiles is not null);
            var sourcePaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var source in project.EnvironmentFiles!)
            {
                Require(!string.IsNullOrWhiteSpace(source) && source.Length <= 4096 && !source.Any(char.IsControl) && !EnvFileSources.IsExample(source));
                var path = EnvFileSources.ResolvePath(project.SourceRoot, source);
                Require(!Directory.Exists(path) && sourcePaths.Add(path));
            }
            Require(project.EditorTargets is not null && project.EditorTargets.All(target => target is not null));
            Names(project.EditorTargets!.Select(target => target.Name));
            foreach (var target in project.EditorTargets)
            {
                Identity(target.Id);
                Require(target.Profile is not null && target.Profile.Length <= 200 && !target.Profile.Any(char.IsControl));
                Require(!string.IsNullOrWhiteSpace(target.Path) && target.Path.Length <= 4096 && !target.Path.Any(char.IsControl));
                var path = EnvFileSources.ResolvePath(project.SourceRoot, target.Path);
                Require(Directory.Exists(path) || path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) && File.Exists(path));
            }
            foreach (var target in project.DeploymentTargets) Identity(target.Id);
            foreach (var release in project.Releases) Identity(release.Id);
            Names(project.Services.Select(service => service.Name));
            var serviceIds = project.Services.Select(service => service.Id).ToHashSet();
            foreach (var service in project.Services)
            {
                Identity(service.Id);
                Require(service.DependsOn.Count == service.DependsOn.Distinct().Count() && service.DependsOn.All(serviceIds.Contains), "missing_reference");
                Require(service.DisabledVariableIds.All(variableIds.Contains), "missing_reference");
                Require(runners.Find(service.RunnerId) is not null, "runner_not_found");
                Require(service.WorkingDirectory is not null && (service.WorkingDirectory.Length == 0 || Path.IsPathFullyQualified(service.WorkingDirectory)));
                Require(service.Settings.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null));
                Require(service.PreSteps.All(step => step is not null && !step.Contains('\0')));
                Require(Enum.IsDefined(service.OutputMode) && Enum.IsDefined(service.RestartPolicy) && Enum.IsDefined(service.Readiness.Kind));
                Require(service.StartDelaySeconds is >= 0 and <= 3600 && service.Readiness.TimeoutSeconds is > 0 and <= 3600);
                Require(service.ExpectedPorts.All(port => port is > 0 and <= 65535));
                Require(service.Readiness.Kind != ReadinessKind.Port || service.Readiness.Port is > 0 and <= 65535);
                Require(service.Readiness.Kind != ReadinessKind.Http || Uri.TryCreate(service.Readiness.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https");
                CheckVariables(service.ExtraVariables);
            }
            var doneServices = new HashSet<Guid>();
            foreach (var service in project.Services)
                Acyclic(service.Id, id => Service(project, id).DependsOn, [], doneServices);
        }
    }
}
