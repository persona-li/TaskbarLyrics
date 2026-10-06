namespace TaskbarLyrics.App.Services;

/// <summary>Owned by the coordinator lock. Only the newest metadata read may publish.</summary>
public sealed class MediaReadState
{
    private long _revision;
    public bool IsPending { get; private set; }
    public long Begin(bool announceChange = true) { IsPending |= announceChange; return ++_revision; }
    public bool Complete(long revision)
    {
        if (revision != _revision) return false;
        IsPending = false;
        return true;
    }
}
