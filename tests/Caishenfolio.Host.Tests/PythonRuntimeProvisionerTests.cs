using Caishenfolio.Host.Python;

namespace Caishenfolio.Host.Tests;

public class PythonRuntimeProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_runtime_tests", Guid.NewGuid().ToString("N"));

    private string StateRoot => Path.Combine(_root, "state");
    private string PythonProject => Path.Combine(_root, "python");

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];
        public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Creates the venv interpreter when `uv venv` runs, mimicking the real tool.</summary>
        public string? VenvInterpreterToCreate { get; set; }

        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (Missing.Contains(command.FileName))
            {
                throw new System.ComponentModel.Win32Exception($"'{command.FileName}' not found");
            }

            var key = $"{command.FileName} {command.Arguments.Split(' ')[0]}";
            var exit = ExitCodes.TryGetValue(key, out var code) ? code : 0;

            if (exit == 0 && command.Arguments.StartsWith("venv") && VenvInterpreterToCreate is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(VenvInterpreterToCreate)!);
                File.WriteAllText(VenvInterpreterToCreate, "");
            }

            return Task.FromResult(new ProcessResult(exit, "", ""));
        }
    }

    private PythonRuntimeOptions NewOptions()
    {
        Directory.CreateDirectory(PythonProject);
        File.WriteAllText(Path.Combine(PythonProject, "pyproject.toml"), "[project]\nname = \"x\"\n");
        return new PythonRuntimeOptions
        {
            StateRoot = StateRoot,
            PythonProjectDirectory = PythonProject,
        };
    }

    [Fact]
    public async Task ProvisionsAVenvThenStampsTheManifestHash()
    {
        var options = NewOptions();
        var runner = new FakeRunner { VenvInterpreterToCreate = options.VenvInterpreter };

        var status = await new PythonRuntimeProvisioner(options, runner).ProvisionAsync();

        Assert.True(status.UvAvailable);
        Assert.True(status.VenvExists);
        Assert.True(status.DependenciesCurrent);
        Assert.Equal(options.VenvInterpreter, status.Interpreter);
        Assert.True(File.Exists(options.MarkerPath));
        Assert.Equal(status.ManifestHash, File.ReadAllText(options.MarkerPath));
        Assert.Contains(runner.Commands, c => c.Arguments.StartsWith("venv"));
        Assert.Contains(runner.Commands, c => c.Arguments.Contains("pip install") && c.Arguments.Contains("[market]"));
    }

    [Fact]
    public async Task SecondRunSkipsInstallBecauseTheManifestIsUnchanged()
    {
        var options = NewOptions();
        var first = new FakeRunner { VenvInterpreterToCreate = options.VenvInterpreter };
        await new PythonRuntimeProvisioner(options, first).ProvisionAsync();

        var second = new FakeRunner();
        var status = await new PythonRuntimeProvisioner(options, second).ProvisionAsync();

        Assert.True(status.IsManagedAndCurrent);
        Assert.DoesNotContain(second.Commands, c => c.Arguments.Contains("pip install"));
    }

    [Fact]
    public async Task ChangingTheManifestMakesTheEnvironmentStale()
    {
        var options = NewOptions();
        var runner = new FakeRunner { VenvInterpreterToCreate = options.VenvInterpreter };
        await new PythonRuntimeProvisioner(options, runner).ProvisionAsync();

        File.WriteAllText(options.ManifestPath, "[project]\nname = \"x\"\ndependencies = [\"pandas\"]\n");

        var stale = await new PythonRuntimeProvisioner(options, new FakeRunner()).InspectAsync();
        Assert.True(stale.VenvExists);
        Assert.False(stale.DependenciesCurrent);
        Assert.Contains("不一致", stale.Summary);

        var again = new FakeRunner { VenvInterpreterToCreate = options.VenvInterpreter };
        var status = await new PythonRuntimeProvisioner(options, again).ProvisionAsync();
        Assert.True(status.DependenciesCurrent);
        Assert.Contains(again.Commands, c => c.Arguments.Contains("pip install"));
    }

    [Fact]
    public async Task WithoutUvItFallsBackToSystemPythonInsteadOfFailing()
    {
        var options = NewOptions();
        var runner = new FakeRunner();
        runner.Missing.Add("uv");

        var status = await new PythonRuntimeProvisioner(options, runner).ProvisionAsync();

        Assert.False(status.UvAvailable);
        Assert.False(status.VenvExists);
        Assert.True(status.CanRun);
        Assert.Equal("python", status.Interpreter);
        Assert.Contains("系统 Python", status.Summary);
    }

    [Fact]
    public async Task WithNeitherUvNorPythonItSaysSoRatherThanThrowing()
    {
        var options = NewOptions();
        var runner = new FakeRunner();
        runner.Missing.Add("uv");
        runner.Missing.Add("python");

        var status = await new PythonRuntimeProvisioner(options, runner).ProvisionAsync();

        Assert.False(status.CanRun);
        Assert.Null(status.Interpreter);
        Assert.Contains("请安装 Python", status.Summary);
    }

    [Fact]
    public async Task AFailedInstallDoesNotStampTheMarker()
    {
        var options = NewOptions();
        var runner = new FakeRunner { VenvInterpreterToCreate = options.VenvInterpreter };
        runner.ExitCodes["uv pip"] = 1;

        var status = await new PythonRuntimeProvisioner(options, runner).ProvisionAsync();

        // A half-finished environment must read as stale next launch, not as ready.
        Assert.True(status.VenvExists);
        Assert.False(status.DependenciesCurrent);
        Assert.False(File.Exists(options.MarkerPath));
    }

    [Fact]
    public async Task VenvAndLogLiveUnderTheStateRoot()
    {
        var options = NewOptions();
        await new PythonRuntimeProvisioner(options, new FakeRunner()).InspectAsync();

        Assert.StartsWith(StateRoot, options.ResolvedVenvDirectory, StringComparison.Ordinal);
        Assert.StartsWith(StateRoot, options.LogPath, StringComparison.Ordinal);
        Assert.True(File.Exists(options.LogPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
