using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;

namespace FastEnvSetter.Tests;

public sealed class EditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fes editor " + Guid.NewGuid());

    public EditorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Arguments_preserve_spaces_and_metacharacters_without_a_shell()
    {
        var executable = Path.Combine(_root, "Code.exe");
        File.WriteAllText(executable, "");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "source & other")).FullName;
        var project = new Project { SourceRoot = _root };
        var start = new EditorLauncher().BuildStart(project, new EditorTarget { Path = "source & other", Profile = "Dev & test; $(value)" }, executable);
        Assert.False(start.UseShellExecute);
        Assert.False(start.Environment.ContainsKey("ELECTRON_RUN_AS_NODE"));
        Assert.Equal(new[] { "--new-window", "--profile", "Dev & test; $(value)", "--", folder }, start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void Missing_root_target_and_executable_are_actionable_errors()
    {
        var launcher = new EditorLauncher();
        Assert.Throws<DirectoryNotFoundException>(() => launcher.BuildStart(new Project(), new EditorTarget()));
        var project = new Project { SourceRoot = _root };
        Assert.Throws<FileNotFoundException>(() => launcher.BuildStart(project, new EditorTarget { Path = "missing" }));
        Assert.Throws<FileNotFoundException>(() => launcher.BuildStart(project, new EditorTarget(), Path.Combine(_root, "absent.exe")));
    }

    [Fact]
    public void Workspace_files_are_supported_but_command_scripts_are_not_executables()
    {
        var executable = Path.Combine(_root, "Code.exe");
        File.WriteAllText(executable, "");
        File.WriteAllText(Path.Combine(_root, "team.code-workspace"), "{}");
        var project = new Project { SourceRoot = _root };
        var start = new EditorLauncher().BuildStart(project, new EditorTarget { Path = "team.code-workspace" }, executable);
        Assert.Equal(new[] { "--new-window", "--", Path.Combine(_root, "team.code-workspace") }, start.ArgumentList);
        var script = Path.Combine(_root, "code.cmd");
        File.WriteAllText(script, "");
        Assert.Throws<FileNotFoundException>(() => new EditorLauncher().BuildStart(project, new EditorTarget(), script));
    }

    [Fact]
    public void Transfer_strips_secret_values_and_credential_references_without_mutating_source()
    {
        var variable = new EnvVariable { Value = "sensitive", ProtectedValue = "encrypted", SecretRef = new SecretRef { ProviderId = "vault", Reference = "credential-id" } };
        var project = new Project { SourceRoot = _root, Services = [new ServiceDefinition { ExtraVariables = [variable] }] };
        var json = ProjectTransfer.Export(project, [], _root, includeSecretReferences: false);
        Assert.DoesNotContain("sensitive", json);
        Assert.DoesNotContain("encrypted", json);
        Assert.DoesNotContain("credential-id", json);
        var imported = ProjectTransfer.Import(ProjectTransfer.Parse(json)!, _root, new AppData());
        var importedVariable = Assert.Single(Assert.Single(imported.Services).ExtraVariables);
        Assert.True(importedVariable.IsSecret);
        Assert.Null(importedVariable.SecretRef);
        Assert.Equal("sensitive", variable.Value);
        Assert.NotNull(variable.SecretRef);
    }

    [Theory]
    [InlineData("{\"Project\":null}")]
    [InlineData("{\"Project\":{\"Services\":[null]}}")]
    [InlineData("{\"Project\":{\"EditorTargets\":[null]}}")]
    public void Malformed_members_are_rejected(string json) => Assert.Null(ProjectTransfer.Parse(json));

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/../../outside")]
    [InlineData("C:\\outside")]
    public void Paths_cannot_escape_root(string path) => Assert.Throws<ArgumentException>(() => ProjectTransfer.ResolvePath(_root, path));

    [Fact]
    public void Trailing_separators_and_working_directory_relative_files_are_portable()
    {
        Assert.Equal(_root, ProjectTransfer.ResolvePath(_root + Path.DirectorySeparatorChar, "."));
        var project = new Project
        {
            SourceRoot = _root,
            Tasks = [new ProjectTask { WorkingDirectory = "src", FileName = Path.Combine(_root, "tools", "build.exe") }],
            DeploymentTargets = [new DeploymentTarget { WorkingDirectory = "deploy", FilePath = "compose.yml" }],
        };
        var package = ProjectTransfer.Parse(ProjectTransfer.Export(project, [], _root))!;
        Assert.Equal(Path.Combine("..", "tools", "build.exe"), package.Project.Tasks[0].FileName);
        var imported = ProjectTransfer.Import(package, _root, new AppData());
        Assert.Equal("compose.yml", imported.DeploymentTargets[0].FilePath);
        Assert.Equal(Path.Combine("..", "tools", "build.exe"), imported.Tasks[0].FileName);
    }

    [Fact]
    public void Dangling_dependencies_are_rejected_without_store_mutation()
    {
        var project = new Project { SourceRoot = _root, Tasks = [new ProjectTask { DependsOn = [Guid.NewGuid()] }] };
        var data = new AppData();
        Assert.Throws<InvalidDataException>(() => ProjectTransfer.Duplicate(project));
        Assert.Throws<InvalidDataException>(() => ProjectTransfer.Import(new ProjectPackage { Project = project }, _root, data));
        Assert.Empty(data.Projects);
    }

    [Fact]
    public void Duplicate_preserves_fields_and_remaps_dependencies()
    {
        var first = new ServiceDefinition { ExtraVariables = [new EnvVariable { DecryptionFailed = true }] };
        var task = new ProjectTask { Name = "Build" };
        var project = new Project
        {
            SourceRoot = _root,
            Tags = ["tag"],
            IsFavorite = true,
            Services = [first, new ServiceDefinition { DependsOn = [first.Id] }],
            Tasks = [task, new ProjectTask { DependsOn = [task.Id] }],
            EditorTargets = [new EditorTarget { Profile = "Development" }],
            DeploymentTargets = [new DeploymentTarget()],
            Releases = [new ProjectRelease()],
            EnvironmentFiles = [".env"],
        };
        var copy = ProjectTransfer.Duplicate(project);
        Assert.NotEqual(project.Id, copy.Id);
        Assert.Equal(copy.Services[0].Id, Assert.Single(copy.Services[1].DependsOn));
        Assert.Equal(copy.Tasks[0].Id, Assert.Single(copy.Tasks[1].DependsOn));
        Assert.NotEqual(project.EditorTargets[0].Id, copy.EditorTargets[0].Id);
        Assert.NotEqual(project.DeploymentTargets[0].Id, copy.DeploymentTargets[0].Id);
        Assert.NotEqual(project.Releases[0].Id, copy.Releases[0].Id);
        Assert.Equal(project.SourceRoot, copy.SourceRoot);
        Assert.Equal(project.EnvironmentFiles, copy.EnvironmentFiles);
        Assert.Equal(project.Tags, copy.Tags);
        Assert.True(copy.IsFavorite);
        Assert.True(copy.Services[0].ExtraVariables[0].DecryptionFailed);
        Assert.NotEqual(first.ExtraVariables[0].Id, copy.Services[0].ExtraVariables[0].Id);
    }

    [Fact]
    public void Transfer_rebases_development_paths_and_rejects_traversal_atomically()
    {
        var project = new Project { SourceRoot = _root, EditorTargets = [new EditorTarget()], Tasks = [new ProjectTask()], EnvironmentFiles = [".env"], DeploymentTargets = [new DeploymentTarget { FilePath = "compose.yml" }] };
        var package = ProjectTransfer.Parse(ProjectTransfer.Export(project, [], _root))!;
        Assert.Equal(".", package.Project.SourceRoot);
        var data = new AppData();
        var imported = ProjectTransfer.Import(package, _root, data);
        Assert.Equal(_root, imported.SourceRoot);
        Assert.Equal(".", imported.EditorTargets[0].Path);
        Assert.Equal(".env", Assert.Single(imported.EnvironmentFiles));
        package.Project.Tasks[0].WorkingDirectory = "../escape";
        Assert.Throws<ArgumentException>(() => ProjectTransfer.Import(package, _root, data));
        Assert.Single(data.Projects);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
