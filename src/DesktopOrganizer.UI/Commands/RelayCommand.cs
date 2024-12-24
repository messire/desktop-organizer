namespace DesktopOrganizer.UI.Commands;

/// <summary>
/// A simple implementation of relay command.
/// </summary>
/// <param name="execute">The delegate that contains the logic to be executed when the command is triggered. </param>
public class RelayCommand(Action execute) : ICommand
{
    /// <summary>
    /// Occurs when changes require the <see cref="CanExecute"/> method to be reevaluated.
    /// Used to notify the UI that execution capability might have been updated.
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Defines whether the command can execute in its current state.
    /// </summary>
    /// <param name="parameter">Command parameter. Not used in this implementation.</param>
    /// <returns><c>true</c> to indicate the command can always execute.</returns>
    public bool CanExecute(object? parameter) => true;

    /// <summary>
    /// Executes the command's logic.
    /// </summary>
    /// <param name="parameter">Command parameter. Not used in this implementation.</param>
    public void Execute(object? parameter) => execute.Invoke();
}
