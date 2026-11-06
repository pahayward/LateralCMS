namespace Lateral.Cms.Persistence;

/// <summary>
/// Serialises writers inside this process.
/// </summary>
/// <remarks>
/// SQLite allows one writer at a time, and a transaction that has read and then tries to write can be
/// refused outright if another writer committed in between. Funnelling ingestion and admin writes through
/// one gate avoids that and keeps each batch's read-decide-write cycle consistent. With a server database
/// the gate can go: the concurrency token on <c>CmsEntity</c> plus the retry in the ingestor cover it.
/// </remarks>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<Releaser> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    public readonly struct Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
