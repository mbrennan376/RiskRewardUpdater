using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public sealed class StateStore
{
    private readonly RiskRewardOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string StatePath => Path.Combine(options.StateFolder, "state.json");
    private string TargetPath => Path.Combine(options.StateFolder, "deployment-target.json");

    public StateStore(IOptions<RiskRewardOptions> options)
    {
        this.options = options.Value;
        Directory.CreateDirectory(this.options.StateFolder);
        Directory.CreateDirectory(Path.Combine(this.options.StateFolder, "published"));
    }

    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StatePath)) return new ApplicationState();
            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<ApplicationState>(stream, JsonDefaults.Options, cancellationToken)
                   ?? new ApplicationState();
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await AtomicWriteAsync(StatePath, state, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<DeploymentTarget> GetTargetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(TargetPath)) return DeploymentTarget.Local;
        try
        {
            var text = await File.ReadAllTextAsync(TargetPath, cancellationToken);
            var setting = JsonSerializer.Deserialize<DeploymentSetting>(text, JsonDefaults.Options);
            return setting?.Target ?? DeploymentTarget.Local;
        }
        catch { return DeploymentTarget.Local; }
    }

    public Task SetTargetAsync(DeploymentTarget target, CancellationToken cancellationToken = default) =>
        AtomicWriteAsync(TargetPath, new DeploymentSetting { Target = target }, cancellationToken);

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ArchiveImage(string ticker, string hash, string sourcePath)
    {
        var folder = Path.Combine(options.StateFolder, "published", ticker.ToUpperInvariant());
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, hash + Path.GetExtension(sourcePath).ToLowerInvariant());
        File.Copy(sourcePath, destination, true);
        return destination;
    }

    private static async Task AtomicWriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken);
        File.Move(temporary, path, true);
    }

    private sealed class DeploymentSetting { public DeploymentTarget Target { get; set; } = DeploymentTarget.Local; }
}
