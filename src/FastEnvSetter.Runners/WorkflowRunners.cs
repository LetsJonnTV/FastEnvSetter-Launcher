using FastEnvSetter.Core;

namespace FastEnvSetter.Runners;

public sealed class RustRunner : RunnerBase
{
    public override string Id => "rust";
    public override string DisplayName => "Rust (Cargo)";
    public override string Icon => "Code24";
    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new("manifest", "Cargo manifest", SettingKind.FilePath) { Default = "Cargo.toml" },
        new("release", "Release", SettingKind.Toggle) { Default = "false" },
        ArgsField(),
    ];
    public override DetectionResult Detect(string folder) => File.Exists(Path.Combine(folder, "Cargo.toml"))
        ? new DetectionResult { Values = new Dictionary<string, string> { ["manifest"] = "Cargo.toml" } } : DetectionResult.Empty;
    protected override ProcessCommand BuildDefault(ServiceDefinition service) =>
        new("cargo", ["run", "--manifest-path", Value(service, "manifest"), .. IsOn(service, "release") ? new[] { "--release" } : [], "--", .. ExtraArgs(service)]);
}

public sealed class JavaRunner : RunnerBase
{
    private const string Gradle = "gradle";
    private const string Maven = "maven";
    private const string BuildTool = "buildTool";
    public override string Id => "java";
    public override string DisplayName => "Java (Maven / Gradle)";
    public override string Icon => "Code24";
    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new(BuildTool, "Build tool", SettingKind.Choice) { Choices = [Maven, Gradle], Default = Maven },
        new("wrapper", "Prefer project wrapper", SettingKind.Toggle) { Default = "true" },
        new("goal", "Run goal / task") { Hint = "Default: exec:java for Maven, run for Gradle. The project must configure that target." },
        ArgsField(),
    ];
    public override DetectionResult Detect(string folder)
    {
        string? tool = null;
        if (File.Exists(Path.Combine(folder, "pom.xml")))
            tool = Maven;
        else if (File.Exists(Path.Combine(folder, "build.gradle")) || File.Exists(Path.Combine(folder, "build.gradle.kts")))
            tool = Gradle;
        return tool is null ? DetectionResult.Empty : new DetectionResult { Values = new Dictionary<string, string> { [BuildTool] = tool } };
    }

    public override IReadOnlyList<string> Validate(ServiceDefinition service)
    {
        if (HasCustomCommand(service))
            return base.Validate(service);
        try
        {
            if (Value(service, BuildTool) is not (Maven or Gradle))
                return ["Select Maven or Gradle."];
            BuildDefault(service);
            return base.Validate(service);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return ["Invalid Java launcher configuration. Windows batch arguments must not contain shell metacharacters."];
        }
    }

    protected override ProcessCommand BuildDefault(ServiceDefinition service)
    {
        var gradle = Value(service, BuildTool) == Gradle;
        var wrapper = gradle ? "gradlew.bat" : "mvnw.cmd";
        var executable = gradle ? Gradle : "mvn";
        if (IsOn(service, "wrapper") && File.Exists(Path.Combine(service.WorkingDirectory, wrapper)))
            executable = Path.GetFullPath(Path.Combine(service.WorkingDirectory, wrapper));
        var goal = Value(service, "goal");
        if (goal.Length == 0)
            goal = gradle ? "run" : "exec:java";
        List<string> args = [.. gradle ? new[] { "--no-daemon" } : [], goal, .. ExtraArgs(service)];
        if (new[] { executable }.Concat(args).Any(part => part.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0))
            throw new ArgumentException("Unsafe Java batch argument.");
        return ProcessCommand.ViaCmd(executable, args);
    }
}

public sealed class CppRunner : RunnerBase
{
    private const string Executable = "executable";
    public override string Id => "cpp";
    public override string DisplayName => "C++ (CMake executable)";
    public override string Icon => "Code24";
    protected override IEnumerable<SettingField> DefineFields() =>
    [
        new(Executable, "Built executable", SettingKind.FilePath) { Required = true, Hint = "Select the executable produced by the CMake build task." },
        ArgsField(),
    ];
    public override DetectionResult Detect(string folder) => File.Exists(Path.Combine(folder, "CMakeLists.txt"))
        ? new DetectionResult { Values = new Dictionary<string, string> { ["args"] = "" } } : DetectionResult.Empty;
    public override IReadOnlyList<string> Validate(ServiceDefinition service)
    {
        var errors = base.Validate(service).ToList();
        if (!HasCustomCommand(service) && Value(service, Executable).Length > 0
            && !File.Exists(Path.GetFullPath(Path.Combine(service.WorkingDirectory, Value(service, Executable)))))
            errors.Add("The selected C++ executable does not exist. Complete the CMake build task first.");
        return errors;
    }
    protected override ProcessCommand BuildDefault(ServiceDefinition service) =>
        new(Path.GetFullPath(Path.Combine(service.WorkingDirectory, Value(service, Executable))), ExtraArgs(service));
}
