using System.Reflection;
using TaskbarLyrics.App.Services;
using Forms = System.Windows.Forms;

internal static class TrayKeyboardChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Construct only the isolated menu; do not create a NotifyIcon or application services.
        var type = typeof(TrayService).Assembly.GetType("TaskbarLyrics.App.Services.RoundedContextMenuStrip", true)!;
        using var menu = (Forms.ContextMenuStrip)Activator.CreateInstance(type, nonPublic: true)!;
        var clicks = 0;
        var first = new Forms.ToolStripMenuItem("&First", null, (_, _) => clicks++);
        var second = new Forms.ToolStripMenuItem("&Second", null, (_, _) => clicks++);
        menu.Items.Add(first);
        menu.Items.Add(second);
        first.Select();
        var selectionBefore = first.Selected;
        var cmd = type.GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dialog = type.GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var character = type.GetMethod("ProcessDialogChar", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mnemonic = type.GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var key in new[]
        {
            Forms.Keys.Control | Forms.Keys.A, Forms.Keys.Control | Forms.Keys.D1,
            Forms.Keys.Up, Forms.Keys.Down, Forms.Keys.Left, Forms.Keys.Right,
            Forms.Keys.Enter, Forms.Keys.Space, Forms.Keys.Escape, Forms.Keys.Tab,
            Forms.Keys.Home, Forms.Keys.End, Forms.Keys.F4, Forms.Keys.F6, Forms.Keys.F10,
            Forms.Keys.Alt | Forms.Keys.Left
        })
        {
            var message = Forms.Message.Create(IntPtr.Zero, 0x0100, (IntPtr)(int)(key & Forms.Keys.KeyCode), IntPtr.Zero);
            check((bool)cmd.Invoke(menu, new object[] { message, key })! && clicks == 0,
                $"Tray command key {key} cannot activate an action");
            check((bool)dialog.Invoke(menu, new object[] { key })! && clicks == 0
                && first.Selected == selectionBefore && !second.Selected,
                $"Tray dialog key {key} cannot navigate, confirm, or cancel");
        }
        check((bool)character.Invoke(menu, new object[] { 'f' })!
            && (bool)mnemonic.Invoke(menu, new object[] { 'f' })! && clicks == 0,
            "Tray text mnemonics cannot activate an action");
        foreach (var key in new[] { Forms.Keys.Alt | Forms.Keys.F4, Forms.Keys.LWin, Forms.Keys.RWin })
        {
            var message = Forms.Message.Create(IntPtr.Zero, 0x0104, (IntPtr)(int)(key & Forms.Keys.KeyCode), IntPtr.Zero);
            check(!(bool)cmd.Invoke(menu, new object[] { message, key })!
                && !(bool)dialog.Invoke(menu, new object[] { key })!,
                $"Tray leaves Windows management key {key} unhandled");
        }
        first.PerformClick();
        check(clicks == 1, "Tray mouse action dispatch remains available");
    }
}
