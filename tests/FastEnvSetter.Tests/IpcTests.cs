using FastEnvSetter.Ipc;

namespace FastEnvSetter.Tests;

public sealed class IpcTests
{
    [Fact]
    public void Default_and_explicit_default_data_path_share_the_same_owner()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastEnvSetter", "data.json");

        Assert.Equal(PipeNames.For(null), PipeNames.For(path));
    }

    [Fact]
    public void Case_and_relative_path_aliases_share_the_same_owner_on_windows()
    {
        var path = Path.GetFullPath("data.json");

        Assert.Equal(PipeNames.For(path), PipeNames.For("data.json"));
        if (OperatingSystem.IsWindows())
            Assert.Equal(PipeNames.For(path.ToUpperInvariant()), PipeNames.For(path.ToLowerInvariant()));
    }

    [Fact]
    public void Different_data_files_remain_isolated()
    {
        Assert.NotEqual(PipeNames.For("one.json"), PipeNames.For("two.json"));
    }
}
