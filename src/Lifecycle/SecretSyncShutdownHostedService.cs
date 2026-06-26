using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;

internal sealed class SecretSyncShutdownHostedService(
    SecretSyncCoordinator coordinator,
    SecretSyncOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(options.ShutdownTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await coordinator.PushAsync(linked.Token);
    }
}
