using TaskbarLyrics.App.Services;
using TaskbarLyrics.Gsmtc;

internal static class SessionReconnectChecks
{
    private sealed record Session(string AppId);
    public static void Run(Action<bool, string> check)
    {
        var old = new Session("QQMusic");
        var reopened = new Session("QQMusic");
        check(SessionPresence.Contains(old, new[] { old }), "Existing session survives unrelated notifications");
        check(!SessionPresence.Contains(old, Array.Empty<Session>()), "Closing player retires its selected session");
        check(!SessionPresence.Contains(old, new[] { reopened }), "Restart with identical app ID requires rebinding");
        check(SessionPresence.Contains(reopened, new[] { reopened }), "Reopened session becomes the live selection");
        check(!SessionPresence.Contains<Session>(null, new[] { reopened }), "No session selection retries discovery");
        var nativeSession = new object();
        var wrapperOne = Tuple.Create(nativeSession);
        var wrapperTwo = Tuple.Create(nativeSession);
        check(SessionPresence.Contains(wrapperOne, new[] { wrapperTwo }, (a, b) => ReferenceEquals(a.Item1, b.Item1)),
            "Distinct projected wrappers for one native session do not cause a disconnect");
        check(!SessionPresence.Contains(wrapperOne, new[] { Tuple.Create(new object()) }, (a, b) => ReferenceEquals(a.Item1, b.Item1)),
            "A replacement native session still requires rebinding");
        var reads = new MediaReadState();
        var beforeClose = reads.Begin();
        reads.Complete(reads.Begin(false));
        check(!reads.Complete(beforeClose) && !reads.IsPending, "Disconnect invalidates an outstanding old metadata read");
        using var requests = new LyricsLoadRequests();
        using var oldLyrics = requests.Begin(1, 1, CancellationToken.None)!;
        requests.Cancel();
        check(oldLyrics.Token.IsCancellationRequested, "Disconnect cancels outstanding old lyrics");
        using var newLyrics = requests.Begin(2, 2, CancellationToken.None)!;
        check(!newLyrics.Token.IsCancellationRequested && requests.Begin(1, 2, CancellationToken.None) is null,
            "Reconnection accepts new lyrics and rejects an old generation");
    }
}
