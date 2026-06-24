using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;

internal sealed class SecretSyncShutdownHostedService(
    SecretSyncCoordinator coordinator,
    SecretSyncOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.ShutdownTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await coordinator.PushAsync(linked.Token);
    }
}
