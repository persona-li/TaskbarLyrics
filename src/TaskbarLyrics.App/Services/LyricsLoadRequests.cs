namespace TaskbarLyrics.App.Services;

/// <summary>One owner per asynchronous load. A retired request never borrows the next request's token.</summary>
public sealed class LyricsLoadRequests : IDisposable
{
    private readonly object _sync = new();
    private Request? _active;
    private bool _disposed;
    public Request? Begin(long generation, long currentGeneration, CancellationToken appToken)
    {
        lock (_sync)
        {
            if (_disposed || generation != currentGeneration || appToken.IsCancellationRequested) return null;
            var next = new Request(this, generation, appToken);
            var previous = _active;
            _active = next;
            previous?.Cancel();
            return next;
        }
    }
    public void Cancel()
    {
        lock (_sync)
        {
            var previous = _active;
            _active = null;
            previous?.Cancel();
        }
    }
    private void Complete(Request request)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, request)) _active = null;
            request.Finish();
        }
    }
    public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; Cancel(); } }

    public sealed class Request : IDisposable
    {
        private readonly LyricsLoadRequests _owner;
        private readonly CancellationTokenSource _source;
        private readonly object _sync = new();
        private int _cancelling;
        private bool _finished, _disposed;
        public long Generation { get; }
        public CancellationToken Token { get; }
        internal Request(LyricsLoadRequests owner, long generation, CancellationToken appToken)
        {
            _owner = owner; Generation = generation;
            _source = CancellationTokenSource.CreateLinkedTokenSource(appToken);
            Token = _source.Token;
        }
        internal void Cancel()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _cancelling++;
                try { _source.Cancel(); }
                catch (AggregateException) { /* A retired callback must not break the replacement request. */ }
                finally { _cancelling--; DisposeIfFinished(); }
            }
        }
        internal void Finish() { lock (_sync) { _finished = true; DisposeIfFinished(); } }
        private void DisposeIfFinished()
        {
            if (!_finished || _cancelling != 0 || _disposed) return;
            _disposed = true; _source.Dispose();
        }
        public void Dispose() => _owner.Complete(this);
    }
}
