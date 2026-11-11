using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Runners;
using System.Text.Json;

namespace FastEnvSetter.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;
    private string DataFile => Path.Combine(_folder, "data.json");
    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void Transaction_preserves_root_and_rolls_back_failed_persistence()
    {
        using var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        var root = store.Data;
        store.CommitConfiguration(data => ++data.Revision);
        Assert.Same(root, store.Data);
        Directory.CreateDirectory(DataFile + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => store.CommitConfiguration(data => ++data.Revision));
        Assert.Equal(1, store.Data.Revision);
        using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        Assert.Equal(1, reloaded.Data.Revision);
    }

    private (JsonAppDataStore Store, ConfigurationService Api, ProcessSupervisor Supervisor) Create(bool allow = true)
    {
        var store = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        store.Data.Settings.AllowExternalConfigurationChanges = allow;
        var runners = new RunnerRegistry([new NodeRunner()]);
        var supervisor = new ProcessSupervisor(runners, []);
        return (store, new ConfigurationService(store, runners, supervisor), supervisor);
    }

    private static ConfigurationResponse Write(ConfigurationService api, JsonAppDataStore store, ConfigurationRequest request)
    {
        request.RequestId ??= Guid.NewGuid();
        request.ExpectedRevision ??= store.Data.Revision;
        return api.Execute(request);
    }

    private static Guid Created(ConfigurationResponse response)
    {
        Assert.True(response.Success, response.Code);
        return response.EntityId!.Value;
    }

    [Fact]
    public void Crud_queries_are_secret_free_and_ordered_assignments_survive_reload()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App") }));
            var first = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Base") }));
            var second = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Override", first) }));
            var variable = Created(Write(api, store, new() { Operation = "create_variable", ProfileId = first, Variable = new("API_KEY", "secret-configuration-value", true) }));
            Created(Write(api, store, new() { Operation = "create_variable", ProfileId = second, Variable = new("API_KEY", "override-secret", true) }));
            Assert.True(Write(api, store, new() { Operation = "set_project_profiles", ProjectId = project, ProfileIds = [first, second] }).Success);
            var service = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Server", "node", Settings: new() { ["customCommand"] = "password-in-settings" }) }));
            Assert.True(Write(api, store, new() { Operation = "update_project", ProjectId = project, Project = new("Renamed") }).Success);
            Assert.True(Write(api, store, new() { Operation = "update_profile", ProfileId = first, Profile = new("Base Updated") }).Success);
            Assert.True(Write(api, store, new() { Operation = "update_variable", ProfileId = first, VariableId = variable, Variable = new("API_KEY", "changed-secret", true) }).Success);
            Assert.True(Write(api, store, new() { Operation = "update_service", ProjectId = project, ServiceId = service, Service = new("Renamed server", "node") }).Success);
            foreach (var request in new ConfigurationRequest[]
            {
                new() { Operation = "get_project", ProjectId = project },
                new() { Operation = "get_profile", ProfileId = first },
                new() { Operation = "get_variable", ProfileId = first, VariableId = variable },
                new() { Operation = "get_service", ProjectId = project, ServiceId = service },
                new() { Operation = "preview_environment", ProjectId = project },
            })
            {
                var response = api.Execute(request);
                Assert.True(response.Success, response.Code);
                var json = JsonSerializer.Serialize(response);
                Assert.DoesNotContain("changed-secret", json);
                Assert.DoesNotContain("override-secret", json);
                Assert.DoesNotContain("password-in-settings", json);
                Assert.DoesNotContain("ProtectedValue", json);
                Assert.DoesNotContain("\"Value\"", json);
            }
            Assert.Equal([first, second], store.Data.Projects[0].EnvProfileIds);
        }
        Assert.DoesNotContain("changed-secret", File.ReadAllText(DataFile));
        using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        Assert.Equal("changed-secret", reloaded.Data.Profiles[0].Variables[0].Value);
        Assert.True(reloaded.Data.Revision > 0);
        Assert.Equal(11, reloaded.Data.ConfigurationReceipts.Count);
    }

    [Fact]
    public void Default_deny_does_not_mutate_memory_or_disk_and_api_cannot_grant_itself()
    {
        var (store, api, supervisor) = Create(false);
        using (store)
        using (supervisor)
        {
            var response = Write(api, store, new() { Operation = "create_project", Project = new("Denied") });
            Assert.Equal("permission_required", response.Code);
            Assert.Empty(store.Data.Projects);
            Assert.Empty(store.Data.ConfigurationReceipts);
            Assert.Equal(0, store.Data.Revision);
            Assert.False(File.Exists(DataFile));
            Assert.Contains("invalid_request", api.ExecuteJson("""{"operation":"create_project","settings":{"allowExternalConfigurationChanges":true}}"""));
            Assert.False(store.Data.Settings.AllowExternalConfigurationChanges);
        }
    }

    [Fact]
    public void Conflicts_and_persisted_replay_do_not_reexecute_or_retain_secret_payloads()
    {
        var requestId = Guid.NewGuid();
        long revision;
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var profileId = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Profile") }));
            var request = new ConfigurationRequest { Operation = "create_variable", ProfileId = profileId, Variable = new("TOKEN", "unreplayable-secret", true), RequestId = requestId };
            var original = Write(api, store, request);
            Assert.True(original.Success);
            revision = original.Revision;
            request.Variable = new("DIFFERENT", "different-payload", true);
            var replay = api.Execute(request);
            Assert.True(replay.Replayed);
            Assert.Equal(original.EntityId, replay.EntityId);
            Assert.Single(store.Data.Profiles[0].Variables);
            Assert.Equal("revision_conflict", api.Execute(new() { Operation = "delete_profile", ProfileId = profileId, RequestId = Guid.NewGuid(), ExpectedRevision = 0 }).Code);
            Assert.Equal(revision, store.Data.Revision);
            Assert.DoesNotContain("unreplayable-secret", JsonSerializer.Serialize(store.Data.ConfigurationReceipts));
            Assert.DoesNotContain("different-payload", File.ReadAllText(DataFile));
        }
        using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
        using var process = new ProcessSupervisor(new RunnerRegistry([]), []);
        var afterRestart = new ConfigurationService(reloaded, new RunnerRegistry([]), process);
        var result = afterRestart.Execute(new() { Operation = "delete_project", RequestId = requestId, ExpectedRevision = 0 });
        Assert.True(result.Replayed);
        Assert.Equal(revision, result.Revision);
    }

    [Fact]
    public void Deletions_remove_dependencies_assignments_and_disabled_references()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var profile = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Parent") }));
            var child = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Child", profile) }));
            var variable = Created(Write(api, store, new() { Operation = "create_variable", ProfileId = profile, Variable = new("X", "1") }));
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App") }));
            var service = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("First", "node") }));
            var dependent = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Second", "node", DependsOn: [service]) }));
            Write(api, store, new() { Operation = "set_project_profiles", ProjectId = project, ProfileIds = [profile, child] });
            Write(api, store, new() { Operation = "set_variable_selection", ProjectId = project, DisabledVariableIds = [variable] });
            Write(api, store, new() { Operation = "set_variable_selection", ProjectId = project, ServiceId = dependent, DisabledVariableIds = [variable] });
            Assert.True(Write(api, store, new() { Operation = "delete_variable", ProfileId = profile, VariableId = variable }).Success);
            Assert.Empty(store.Data.Projects[0].DisabledVariableIds);
            Assert.Empty(store.Data.Projects[0].Services[1].DisabledVariableIds);
            Assert.True(Write(api, store, new() { Operation = "delete_service", ProjectId = project, ServiceId = service }).Success);
            Assert.Empty(store.Data.Projects[0].Services[0].DependsOn);
            Assert.True(Write(api, store, new() { Operation = "delete_profile", ProfileId = profile }).Success);
            Assert.Null(store.Data.Profiles[0].ParentId);
            Assert.Equal([child], store.Data.Projects[0].EnvProfileIds);
            Assert.True(Write(api, store, new() { Operation = "delete_project", ProjectId = project }).Success);
            Assert.Empty(store.Data.Projects);
        }
    }

    [Fact]
    public void Invalid_cycles_duplicates_and_missing_references_rollback()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var first = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("First") }));
            var second = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Second", first) }));
            Assert.Equal("cycle_detected", Write(api, store, new() { Operation = "update_profile", ProfileId = first, Profile = new("First", second) }).Code);
            Assert.Null(store.Data.Profiles[0].ParentId);
            Created(Write(api, store, new() { Operation = "create_variable", ProfileId = first, Variable = new("NAME", "1") }));
            Assert.False(Write(api, store, new() { Operation = "create_variable", ProfileId = first, Variable = new("name", "2") }).Success);
            Assert.Single(store.Data.Profiles[0].Variables);
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App") }));
            Assert.False(Write(api, store, new() { Operation = "set_project_profiles", ProjectId = project, ProfileIds = [first, first] }).Success);
            Assert.False(Write(api, store, new() { Operation = "set_project_profiles", ProjectId = project, ProfileIds = [Guid.NewGuid()] }).Success);
            var service = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Server", "node") }));
            Assert.Equal("cycle_detected", Write(api, store, new() { Operation = "update_service", ProjectId = project, ServiceId = service, Service = new("Server", "node", DependsOn: [service]) }).Code);
            Assert.Empty(store.Data.Projects[0].Services[0].DependsOn);
        }
    }

    [Fact]
    public void Batch_rolls_back_all_changes_and_uses_one_revision()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var batch = new ConfigurationRequest { Operation = "batch", Changes = [new() { Operation = "create_project", Project = new("App") }, new() { Operation = "create_profile", Profile = new("") }] };
            Assert.False(Write(api, store, batch).Success);
            Assert.Empty(store.Data.Projects);
            Assert.Equal(0, store.Data.Revision);
            batch.Changes[1].Profile = new("Profile");
            Assert.True(api.Execute(batch).Success);
            Assert.Equal(1, store.Data.Revision);
            Assert.Single(store.Data.ConfigurationReceipts);
            Assert.True(api.Execute(batch).Replayed);
        }
    }

    [Fact]
    public void Persistence_failure_does_not_consume_request_id_and_retry_succeeds()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            Directory.CreateDirectory(DataFile + ".tmp");
            var request = new ConfigurationRequest { Operation = "create_project", Project = new("App") };
            Assert.Equal("persistence_failed", Write(api, store, request).Code);
            Assert.Empty(store.Data.Projects);
            Assert.Empty(store.Data.ConfigurationReceipts);
            Directory.Delete(DataFile + ".tmp");
            Assert.True(api.Execute(request).Success);
            Assert.Single(store.Data.Projects);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{bad")]
    [InlineData("{\"version\":2,\"operation\":\"list_projects\"}")]
    [InlineData("{\"operation\":null}")]
    [InlineData("{\"operation\":\"create_profile\",\"profile\":null}")]
    [InlineData("{\"operation\":\"list_projects\",\"unknown\":true}")]
    public void Bad_requests_are_structured_and_do_not_mutate(string json)
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            using var response = JsonDocument.Parse(api.ExecuteJson(json));
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(0, store.Data.Revision);
            Assert.False(File.Exists(DataFile));
            Assert.False(api.Execute(null).Success);
            Assert.Contains("invalid_request", api.ExecuteJson(new string(' ', ConfigurationService.MaximumRequestLength + 1)));
        }
    }

    [Fact]
    public void Caller_cannot_mutate_committed_state_through_request_lists()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var tags = new List<string> { "safe" };
            Created(Write(api, store, new() { Operation = "create_project", Project = new("App", Tags: tags) }));
            tags.Add("unexpected");
            Assert.Equal(["safe"], store.Data.Projects[0].Tags);
            store.SaveNow();
            Assert.Equal(2, store.Data.Revision);
        }
    }

    [Fact]
    public void Active_service_edits_deletes_and_override_changes_are_rejected()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App") }));
            var service = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Server", "node") }));
            var activeApi = new ConfigurationService(store, new RunnerRegistry([new NodeRunner()]), new ActiveSupervisor());
            foreach (var request in new ConfigurationRequest[]
            {
                new() { Operation = "update_service", ProjectId = project, ServiceId = service, Service = new("Changed", "node") },
                new() { Operation = "delete_service", ProjectId = project, ServiceId = service },
                new() { Operation = "create_variable", ProjectId = project, ServiceId = service, Variable = new("X", "secret", true) },
                new() { Operation = "set_variable_selection", ProjectId = project, ServiceId = service, DisabledVariableIds = [] },
                new() { Operation = "delete_project", ProjectId = project },
            })
                Assert.Equal("service_active", Write(activeApi, store, request).Code);
            Assert.Equal(2, store.Data.Revision);
            Assert.Equal("Server", store.Data.Projects[0].Services[0].Name);
        }
    }

    [Fact]
    public void Discovery_validation_and_provenance_return_metadata_only()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var profile = Created(Write(api, store, new() { Operation = "create_profile", Profile = new("Base") }));
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App") }));
            Write(api, store, new() { Operation = "create_variable", ProfileId = profile, Variable = new("TOKEN", SecretRef: new() { ProviderId = "provider", Reference = "secret-vault-reference" }) });
            Write(api, store, new() { Operation = "set_project_profiles", ProjectId = project, ProfileIds = [profile] });
            foreach (var operation in new[] { "capabilities", "list_projects", "list_profiles", "list_services", "list_variables", "list_runners", "runner_schema", "validate", "preview_environment" })
            {
                var response = api.Execute(new() { Operation = operation, ProjectId = operation == "list_variables" ? null : project, ProfileId = profile, RunnerId = "node" });
                Assert.True(response.Success, response.Code);
                Assert.DoesNotContain("secret-vault-reference", JsonSerializer.Serialize(response));
            }
            var preview = (JsonElement)api.Execute(new() { Operation = "preview_environment", ProjectId = project }).Data!;
            Assert.Equal(profile.ToString(), preview.GetProperty("variables")[0].GetProperty("profileId").GetString());
            Assert.False(preview.GetProperty("fileSourcesEvaluated").GetBoolean());
        }
    }

    [Fact]
    public void Malformed_null_persisted_lists_are_rejected_without_overwrite()
    {
        const string contents = """{"Profiles":null,"Projects":[]}""";
        File.WriteAllText(DataFile, contents);
        Assert.Throws<InvalidDataException>(() => new JsonAppDataStore(new DpapiSecretProtector(), DataFile));
        Assert.Equal(contents, File.ReadAllText(DataFile));
    }

    [Fact]
    public void Malformed_typed_payloads_do_not_crash_or_commit()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            foreach (var request in new ConfigurationRequest[]
            {
                new() { Operation = "batch", Changes = [null!] },
                new() { Operation = "create_profile", Profile = new(null!) },
                new() { Operation = "create_project", Project = new("App", Tags: [null!]) },
                new() { Operation = "set_project_profiles", ProjectId = Guid.NewGuid(), ProfileIds = null },
            })
                Assert.False(Write(api, store, request).Success);
            Assert.Empty(store.Data.ConfigurationReceipts);
            Assert.Equal(0, store.Data.Revision);
        }
    }

    [Fact]
    public void Metadata_replacements_persist_valid_dag_and_return_no_command_values()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App", _folder, true, ["retained"]) }));
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var editor = Guid.NewGuid();
            var response = Write(api, store, new()
            {
                Operation = "set_project_tasks",
                ProjectId = project,
                Tasks = [new(second, "Test", ProjectTaskKind.Test, "private-executable", ["argument-secret"], DependsOn: [first], TimeoutSeconds: 86400),
                    new(first, "Build", ProjectTaskKind.Build, "dotnet", [], TimeoutSeconds: 1)],
            });
            Assert.True(response.Success, response.Code);
            Assert.True(Write(api, store, new() { Operation = "set_project_env_files", ProjectId = project, EnvironmentFiles = [".env.missing"] }).Success);
            Assert.True(Write(api, store, new() { Operation = "set_editor_targets", ProjectId = project, EditorTargets = [new(editor, "Editor")] }).Success);
            foreach (var operation in new[] { "get_project", "list_projects", "validate", "capabilities" })
            {
                var read = api.Execute(new() { Operation = operation, ProjectId = project });
                Assert.True(read.Success, read.Code);
                var json = JsonSerializer.Serialize(read);
                Assert.DoesNotContain("argument-secret", json);
                Assert.DoesNotContain("private-executable", json);
            }
            Assert.DoesNotContain("argument-secret", JsonSerializer.Serialize(response));
            var view = (JsonElement)api.Execute(new() { Operation = "get_project", ProjectId = project }).Data!;
            Assert.Equal(2, view.GetProperty("taskCount").GetInt32());
            Assert.Equal(1, view.GetProperty("environmentFileCount").GetInt32());
            Assert.Equal(editor, view.GetProperty("editorTargets")[0].GetProperty("id").GetGuid());
            Assert.True(store.Data.Projects[0].IsFavorite);
            Assert.Equal(["retained"], store.Data.Projects[0].Tags);
            using var reloaded = new JsonAppDataStore(new DpapiSecretProtector(), DataFile);
            Assert.Equal([second, first], reloaded.Data.Projects[0].Tasks.Select(task => task.Id));
            Assert.Equal(["argument-secret"], reloaded.Data.Projects[0].Tasks[0].Arguments);
        }
    }

    [Theory]
    [InlineData("set_project_tasks")]
    [InlineData("set_project_env_files")]
    [InlineData("set_editor_targets")]
    public void Metadata_replacements_enforce_permissions_revisions_and_replay(string operation)
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App", _folder) }));
            var request = new ConfigurationRequest { Operation = operation, ProjectId = project, Tasks = [], EnvironmentFiles = [], EditorTargets = [] };
            store.Data.Settings.AllowExternalConfigurationChanges = false;
            Assert.Equal("permission_required", Write(api, store, request).Code);
            store.Data.Settings.AllowExternalConfigurationChanges = true;
            request.ExpectedRevision = 0;
            Assert.Equal("revision_conflict", api.Execute(request).Code);
            Assert.Equal(1, store.Data.Revision);
            request.ExpectedRevision = 1;
            Assert.True(api.Execute(request).Success);
            Assert.True(api.Execute(request).Replayed);
            Assert.Equal(2, store.Data.Revision);
            var capabilities = (JsonElement)api.Execute(new() { Operation = "capabilities" }).Data!;
            Assert.Contains(operation, capabilities.GetProperty("writes").EnumerateArray().Select(value => value.GetString()));
        }
    }

    [Fact]
    public void Invalid_metadata_replacements_rollback_without_echoing_arguments()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App", _folder) }));
            var taskId = Guid.NewGuid();
            ProjectTaskConfiguration Task(Guid? id = null, string path = ".", List<Guid>? dependencies = null, List<string>? arguments = null, int timeout = 600, string fileName = "dotnet") =>
                new(id ?? taskId, "Task", ProjectTaskKind.Test, fileName, arguments ?? ["sensitive-argument"], path, dependencies, timeout);
            Assert.True(Write(api, store, new() { Operation = "set_project_tasks", ProjectId = project, Tasks = [Task()] }).Success);
            var before = File.ReadAllText(DataFile);
            var revision = store.Data.Revision;
            foreach (var request in new ConfigurationRequest[]
            {
                new() { Operation = "set_project_tasks", Tasks = null },
                new() { Operation = "set_project_tasks", Tasks = [null!] },
                new() { Operation = "set_project_tasks", Tasks = [Task(Guid.Empty)] },
                new() { Operation = "set_project_tasks", Tasks = [Task(project)] },
                new() { Operation = "set_project_tasks", Tasks = [Task(), Task()] },
                new() { Operation = "set_project_tasks", Tasks = [Task(path: "../outside")] },
                new() { Operation = "set_project_tasks", Tasks = [Task(path: _folder)] },
                new() { Operation = "set_project_tasks", Tasks = [Task(fileName: "../outside.exe")] },
                new() { Operation = "set_project_tasks", Tasks = [Task(dependencies: [taskId])] },
                new() { Operation = "set_project_tasks", Tasks = [Task(dependencies: [Guid.NewGuid()])] },
                new() { Operation = "set_project_tasks", Tasks = [Task(arguments: [null!])] },
                new() { Operation = "set_project_tasks", Tasks = [Task(arguments: [new string('x', 32769)])] },
                new() { Operation = "set_project_tasks", Tasks = [Task(arguments: ["bad\0value"])] },
                new() { Operation = "set_project_tasks", Tasks = [Task(timeout: 0)] },
                new() { Operation = "set_project_tasks", Tasks = [Task(timeout: 86401)] },
                new() { Operation = "set_project_tasks", Tasks = [Task(arguments: Enumerable.Repeat("", 1025).ToList())] },
                new() { Operation = "set_project_tasks", Tasks = [Task(fileName: new string('x', 4097))] },
                new() { Operation = "set_project_tasks", Tasks = [Task() with { Arguments = null! }] },
                new() { Operation = "set_project_tasks", Tasks = [Task() with { Kind = (ProjectTaskKind)999 }] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = null },
                new() { Operation = "set_project_env_files", EnvironmentFiles = [null!] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = ["../outside.env"] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = [".env.example"] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = [".env-example"] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = ["."] },
                new() { Operation = "set_project_env_files", EnvironmentFiles = [".env", "./.env"] },
                new() { Operation = "set_editor_targets", EditorTargets = null },
                new() { Operation = "set_editor_targets", EditorTargets = [null!] },
                new() { Operation = "set_editor_targets", EditorTargets = [new(Guid.Empty, "Editor")] },
                new() { Operation = "set_editor_targets", EditorTargets = [new(taskId, "Editor")] },
                new() { Operation = "set_editor_targets", EditorTargets = [new(Guid.NewGuid(), "Editor", "../outside")] },
                new() { Operation = "set_editor_targets", EditorTargets = [new(Guid.NewGuid(), "Editor", ".", "bad\0profile")] },
            })
            {
                request.ProjectId = project;
                var response = Write(api, store, request);
                Assert.False(response.Success, request.Operation);
                Assert.DoesNotContain("sensitive-argument", JsonSerializer.Serialize(response));
                Assert.Equal(revision, store.Data.Revision);
                Assert.Equal(before, File.ReadAllText(DataFile));
                Assert.Single(store.Data.Projects[0].Tasks);
                Assert.Empty(store.Data.Projects[0].EnvironmentFiles);
                Assert.Empty(store.Data.Projects[0].EditorTargets);
            }
        }
    }

    [Fact]
    public void Task_and_env_replacements_guard_active_services_but_editor_changes_do_not()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App", _folder) }));
            Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Server", "node") }));
            var active = new ConfigurationService(store, new RunnerRegistry([new NodeRunner()]), new ActiveSupervisor());
            Assert.Equal("service_active", Write(active, store, new() { Operation = "set_project_tasks", ProjectId = project, Tasks = [] }).Code);
            Assert.Equal("service_active", Write(active, store, new() { Operation = "set_project_env_files", ProjectId = project, EnvironmentFiles = [] }).Code);
            Assert.True(Write(active, store, new() { Operation = "set_editor_targets", ProjectId = project, EditorTargets = [new(Guid.NewGuid(), "Editor")] }).Success);
            Assert.Equal(3, store.Data.Revision);
            Assert.Single(store.Data.Projects[0].Services);
        }
    }

    [Fact]
    public void Metadata_batch_updates_and_clears_collections_without_changing_other_fields()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var project = Created(Write(api, store, new() { Operation = "create_project", Project = new("App", _folder, true, ["tag"]) }));
            var service = Created(Write(api, store, new() { Operation = "create_service", ProjectId = project, Service = new("Server", "node") }));
            var task = new ProjectTaskConfiguration(Guid.NewGuid(), "Build", ProjectTaskKind.Build, "dotnet", ["build"]);
            var editor = new EditorTargetConfiguration(Guid.NewGuid(), "Editor");
            ConfigurationRequest Batch(List<ProjectTaskConfiguration> tasks, List<string> files, List<EditorTargetConfiguration> editors) => new()
            {
                Operation = "batch",
                Changes =
                [
                    new() { Operation = "set_project_tasks", ProjectId = project, Tasks = tasks },
                    new() { Operation = "set_project_env_files", ProjectId = project, EnvironmentFiles = files },
                    new() { Operation = "set_editor_targets", ProjectId = project, EditorTargets = editors },
                ],
            };
            Assert.True(Write(api, store, Batch([task], [".env"], [editor])).Success);
            Assert.Equal(3, store.Data.Revision);
            Assert.True(Write(api, store, new() { Operation = "set_project_tasks", ProjectId = project, Tasks = [task with { Name = "Updated" }] }).Success);
            Assert.Equal(task.Id, store.Data.Projects[0].Tasks[0].Id);
            Assert.Equal([".env"], store.Data.Projects[0].EnvironmentFiles);
            Assert.Equal(editor.Id, store.Data.Projects[0].EditorTargets[0].Id);
            var before = File.ReadAllText(DataFile);
            Assert.False(Write(api, store, Batch([], ["../escape"], [])).Success);
            Assert.Equal(before, File.ReadAllText(DataFile));
            Assert.Equal("Updated", store.Data.Projects[0].Tasks[0].Name);
            Assert.True(Write(api, store, Batch([], [], [])).Success);
            Assert.Empty(store.Data.Projects[0].Tasks);
            Assert.Empty(store.Data.Projects[0].EnvironmentFiles);
            Assert.Empty(store.Data.Projects[0].EditorTargets);
            Assert.Equal(service, store.Data.Projects[0].Services[0].Id);
            Assert.Equal("App", store.Data.Projects[0].Name);
            Assert.Equal(_folder, store.Data.Projects[0].SourceRoot);
            Assert.True(store.Data.Projects[0].IsFavorite);
            Assert.Equal(["tag"], store.Data.Projects[0].Tags);
        }
    }

    [Fact]
    public void Metadata_ids_are_global_and_multi_task_cycles_are_rejected()
    {
        var (store, api, supervisor) = Create();
        using (store)
        using (supervisor)
        {
            var first = Created(Write(api, store, new() { Operation = "create_project", Project = new("First", _folder) }));
            var second = Created(Write(api, store, new() { Operation = "create_project", Project = new("Second", _folder) }));
            var editorId = Guid.NewGuid();
            Assert.True(Write(api, store, new() { Operation = "set_editor_targets", ProjectId = first, EditorTargets = [new(editorId, "Editor")] }).Success);
            Assert.False(Write(api, store, new() { Operation = "set_editor_targets", ProjectId = second, EditorTargets = [new(editorId, "Other editor")] }).Success);
            Assert.False(Write(api, store, new() { Operation = "set_project_tasks", ProjectId = second, Tasks = [new(editorId, "Task", ProjectTaskKind.Build, "dotnet", [])] }).Success);
            var firstTask = Guid.NewGuid();
            var secondTask = Guid.NewGuid();
            Assert.False(Write(api, store, new()
            {
                Operation = "set_project_tasks",
                ProjectId = second,
                Tasks = [new(firstTask, "First", ProjectTaskKind.Build, "dotnet", [], DependsOn: [secondTask]),
                    new(secondTask, "Second", ProjectTaskKind.Test, "dotnet", [], DependsOn: [firstTask])],
            }).Success);
            Assert.Equal(3, store.Data.Revision);
            Assert.Empty(store.Data.Projects[1].Tasks);
            Assert.Empty(store.Data.Projects[1].EditorTargets);
        }
    }

    private sealed class ActiveSupervisor : IProcessSupervisor
    {
        public event Action<ServiceStateChange>? StateChanged { add { } remove { } }
        public ServiceState GetState(Guid serviceId) => ServiceState.Running;
        public string? GetMessage(Guid serviceId) => null;
        public bool IsAnyActive => true;
        public IReadOnlyList<LogEntry> GetLogSince(Guid serviceId, long afterSequence) => [];
        public void ClearLog(Guid serviceId) { }
        public Task<StartResult> StartProjectAsync(Project project, IReadOnlyList<EnvProfile> profiles, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StartResult> StartServiceAsync(Project project, ServiceDefinition service, IReadOnlyList<EnvProfile> profiles) => throw new NotSupportedException();
        public Task StopServiceAsync(Guid serviceId) => throw new NotSupportedException();
        public Task StopProjectAsync(Project project) => throw new NotSupportedException();
        public Task StopAllAsync() => throw new NotSupportedException();
    }
}
