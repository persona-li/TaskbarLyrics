using System.Runtime.InteropServices;
namespace TaskbarLyrics.Gsmtc;

public static class SessionPresence
{
    // WinRT can project the same native session into different managed wrappers.
    // Compare canonical COM identity, never the reusable application ID.
    public static bool Contains<T>(T? selected, IEnumerable<T> available) where T : class =>
        Contains(selected, available, SameInstance);

    public static bool Contains<T>(T? selected, IEnumerable<T> available, Func<T, T, bool> sameInstance) where T : class =>
        selected is not null && available.Any(session => sameInstance(selected, session));

    public static bool SameInstance(object left, object right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is not WinRT.IWinRTObject a || right is not WinRT.IWinRTObject b) return false;
        nint first = 0, second = 0;
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        try
        {
            return Marshal.QueryInterface(a.NativeObject.ThisPtr, in iid, out first) >= 0
                && Marshal.QueryInterface(b.NativeObject.ThisPtr, in iid, out second) >= 0
                && first != 0 && first == second;
        }
        finally
        {
            if (first != 0) Marshal.Release(first);
            if (second != 0) Marshal.Release(second);
            GC.KeepAlive(left); GC.KeepAlive(right);
        }
    }
}
