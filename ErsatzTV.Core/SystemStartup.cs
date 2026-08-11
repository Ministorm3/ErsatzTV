namespace ErsatzTV.Core;

public class SystemStartup : IDisposable
{
    private readonly SemaphoreSlim _databaseCleaned = new(0, 100);
    private readonly SemaphoreSlim _databaseStartup = new(0, 100);
    private readonly SemaphoreSlim _searchIndexStartup = new(0, 100);
    private readonly SemaphoreSlim _transcodeFolderStartup = new(0, 100);

    private bool _disposedValue;

    public bool IsDatabaseReady { get; private set; }
    public bool IsDatabaseCleaned { get; private set; }
    public bool IsSearchIndexReady { get; private set; }
    public bool IsTranscodeFolderReady { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event EventHandler OnDatabaseReady;
    public event EventHandler OnDatabaseCleaned;
    public event EventHandler OnSearchIndexReady;
    public event EventHandler OnTranscodeFolderReady;

    public async Task WaitForDatabase(CancellationToken cancellationToken) =>
        await _databaseStartup.WaitAsync(cancellationToken);

    public async Task WaitForDatabaseCleaned(CancellationToken cancellationToken) =>
        await _databaseCleaned.WaitAsync(cancellationToken);

    public async Task WaitForSearchIndex(CancellationToken cancellationToken) =>
        await _searchIndexStartup.WaitAsync(cancellationToken);

    // Unlike the other phases, this one is awaited on every channel session
    // start for the life of the process, so it short-circuits on the flag
    // instead of consuming one of the semaphore's 100 permits per call.
    public async Task WaitForTranscodeFolder(CancellationToken cancellationToken)
    {
        if (IsTranscodeFolderReady)
        {
            return;
        }

        await _transcodeFolderStartup.WaitAsync(cancellationToken);
    }

    public void DatabaseIsReady()
    {
        _databaseStartup.Release(100);
        IsDatabaseReady = true;
        OnDatabaseReady?.Invoke(this, EventArgs.Empty);
    }

    public void DatabaseIsCleaned()
    {
        _databaseCleaned.Release(100);
        IsDatabaseCleaned = true;
        OnDatabaseCleaned?.Invoke(this, EventArgs.Empty);
    }

    public void SearchIndexIsReady()
    {
        _searchIndexStartup.Release(100);
        IsSearchIndexReady = true;
        OnSearchIndexReady?.Invoke(this, EventArgs.Empty);
    }

    public void TranscodeFolderIsReady()
    {
        IsTranscodeFolderReady = true;
        _transcodeFolderStartup.Release(100);
        OnTranscodeFolderReady?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _databaseStartup.Dispose();
                _databaseCleaned.Dispose();
                _searchIndexStartup.Dispose();
                _transcodeFolderStartup.Dispose();
            }

            _disposedValue = true;
        }
    }
}
