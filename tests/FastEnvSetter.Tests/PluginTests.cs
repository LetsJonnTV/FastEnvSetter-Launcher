using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using FastEnvSetter.Infrastructure.Plugins;

namespace FastEnvSetter.Tests;

public sealed class PluginTests : IDisposable
{
    private const string StyleManifest = """{"Id":"blue","Name":"Blau","Version":"1.0.0","Publisher":"x","ApiVersion":"1.0","Kind":"style"}""";
    private const string Theme = """{"Base":"Dark","Accent":"#112233"}""";

    private readonly string _folder = Directory.CreateTempSubdirectory("fesplug").FullName;
    private readonly JsonAppDataStore _store;
    private readonly PluginManager _manager;

    public PluginTests()
    {
        _store = new JsonAppDataStore(new DpapiSecretProtector(), Path.Combine(_folder, "data.json"));
        _manager = new PluginManager(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".fesplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public void Style_plugin_installs_and_provides_theme_until_disabled()
    {
        var installed = _manager.Install(_manager.Inspect(Zip(("plugin.json", StyleManifest), ("theme.json", Theme)), "test"));

        Assert.Equal("#112233", _manager.GetThemes().Single().Definition.Accent);

        _manager.SetEnabled(installed.Id, false);
        Assert.Empty(_manager.GetThemes());

        _manager.Uninstall(installed.Id);
        Assert.False(Directory.Exists(Path.Combine(_manager.Root, "blue")));
    }

    [Theory]
    [InlineData("evil.dll")]
    [InlineData("view.xaml")]
    public void Style_plugin_must_not_carry_code(string file) =>
        Assert.Throws<PluginException>(() => _manager.Inspect(Zip(("plugin.json", StyleManifest), ("theme.json", Theme), (file, "x")), "test"));

    [Theory]
    [InlineData("../boese.txt")]
    [InlineData("a/../../boese.txt")]
    public void Zip_slip_is_rejected(string entry) =>
        Assert.Throws<PluginException>(() => _manager.Inspect(Zip(("plugin.json", StyleManifest), ("theme.json", Theme), (entry, "x")), "test"));

    [Theory]
    [InlineData("""{"Id":"blue","Name":"B","Version":"1.0.0","Publisher":"x","ApiVersion":"2.0","Kind":"style"}""")]
    [InlineData("""{"Id":"blue","Name":"B","Version":"1.0.0","Publisher":"x","ApiVersion":"1.9","Kind":"style"}""")]
    [InlineData("""{"Id":"../x","Name":"B","Version":"1.0.0","Publisher":"x","ApiVersion":"1.0","Kind":"style"}""")]
    [InlineData("""{"Id":"blue","Name":"B","Version":"eins","Publisher":"x","ApiVersion":"1.0","Kind":"style"}""")]
    [InlineData("""{"Id":"blue","Name":"B","Version":"1.0.0","Publisher":"x","ApiVersion":"1.0","Kind":"anders"}""")]
    public void Invalid_manifests_are_rejected(string manifest) =>
        Assert.Throws<PluginException>(() => _manager.Inspect(Zip(("plugin.json", manifest), ("theme.json", Theme)), "test"));

    [Fact]
    public void Invalid_theme_color_is_rejected() =>
        Assert.Throws<PluginException>(() => _manager.Inspect(Zip(("plugin.json", StyleManifest), ("theme.json", """{"Base":"Dark","Accent":"rot"}""")), "test"));

    private static string SampleDll()
    {
        var parts = AppContext.BaseDirectory.TrimEnd('\\').Split('\\');
        var configuration = parts[^2];
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "samples", "SampleRunnerPlugin", "bin", configuration, "net10.0-windows", "SampleRunnerPlugin.dll");
    }

    private string SamplePackage()
    {
        var path = Path.Combine(_folder, "sample.fesplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(SampleDll(), "SampleRunnerPlugin.dll");
        using var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open());
        writer.Write(File.ReadAllText(Path.Combine(Path.GetDirectoryName(SampleDll())!, "..", "..", "..", "plugin.json")));
        return path;
    }

    [Fact]
    public void Code_plugin_loads_and_tampering_blocks_it()
    {
        var installed = _manager.Install(_manager.Inspect(SamplePackage(), "test"));

        var result = _manager.LoadCodePlugins();
        Assert.Empty(result.Warnings);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        result.Plugins.Single().ConfigureServices(services);
        Assert.Contains(services, s => s.ServiceType == typeof(IRunner));

        File.AppendAllText(Path.Combine(_manager.Root, installed.Id, installed.Version, "SampleRunnerPlugin.dll"), "x");
        var tampered = _manager.LoadCodePlugins();
        Assert.Empty(tampered.Plugins);
        Assert.Contains("verändert", tampered.Warnings.Single());
    }

    [Fact]
    public void Failed_boot_disables_last_activated_plugin_only_once()
    {
        var installed = _manager.Install(_manager.Inspect(SamplePackage(), "test"));

        Assert.Null(_manager.BeginBoot());
        Assert.Equal(installed.Name, _manager.BeginBoot());
        Assert.False(_store.Data.Plugins.Single().Enabled);
        Assert.Null(_manager.BeginBoot());

        _manager.CompleteBoot();
        Assert.False(File.Exists(Path.Combine(_manager.Root, ".boot")));
    }

    [Theory]
    [InlineData("ghcr.io/a/b:1.0.0", "ghcr.io", "a/b", "1.0.0")]
    [InlineData("ghcr.io/a/b/c", "ghcr.io", "a/b/c", "latest")]
    [InlineData("ghcr.io/a/b@sha256:abc", "ghcr.io", "a/b", "sha256:abc")]
    public void References_parse(string text, string host, string repository, string tag) =>
        Assert.Equal(new PluginReference(host, repository, tag), PluginReference.Parse(text));

    private static (HttpListener Listener, string Host) Serve(byte[] blob, string blobDigest, string? manifestDigestOverride = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var host = $"localhost:{port}";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}/");
        listener.Start();
        var manifest = JsonSerializer.Serialize(new
        {
            layers = new[] { new { mediaType = OciPluginRegistry.LayerMediaType, digest = manifestDigestOverride ?? blobDigest, size = blob.Length } },
        });

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var path = context.Request.Url!.AbsolutePath;
                var body = path.StartsWith("/token") ? Encoding.UTF8.GetBytes("""{"token":"t"}""")
                    : path.Contains("/manifests/") ? Encoding.UTF8.GetBytes(manifest)
                    : blob;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return (listener, host);
    }

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Oci_download_installs_verified_package()
    {
        var blob = File.ReadAllBytes(Zip(("plugin.json", StyleManifest), ("theme.json", Theme)));
        var (listener, host) = Serve(blob, Digest(blob));
        using (listener)
        using (var http = new HttpClient())
        {
            var registry = new OciPluginRegistry(http, host, "http");

            var path = await registry.DownloadAsync(PluginReference.Parse($"{host}/a/blue:1.0.0")!);

            var candidate = _manager.Inspect(path, "oci");
            Assert.Equal(Digest(blob), candidate.Digest);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Oci_digest_mismatch_is_rejected()
    {
        var blob = Encoding.UTF8.GetBytes("inhalt");
        var (listener, host) = Serve(blob, Digest(blob), "sha256:" + new string('0', 64));
        using (listener)
        using (var http = new HttpClient())
        {
            var registry = new OciPluginRegistry(http, host, "http");
            await Assert.ThrowsAsync<PluginException>(() => registry.DownloadAsync(PluginReference.Parse($"{host}/a/b:1")!));
        }
    }

    [Fact]
    public async Task Foreign_registry_is_rejected()
    {
        using var http = new HttpClient();
        var registry = new OciPluginRegistry(http);
        await Assert.ThrowsAsync<PluginException>(() => registry.DownloadAsync(PluginReference.Parse("evil.example/a/b:1")!));
    }
}
