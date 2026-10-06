using System.Windows.Input;
namespace TaskbarLyrics.App.Services;

// Avoid ThumbButtonInfo coercing IsEnabled during a transient busy state.
public sealed class TaskbarPreviewCommand(ICommand command) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
