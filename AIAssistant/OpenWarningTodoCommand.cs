using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AIAssistant
{
    /// <summary>
    /// Command to open the Warning Fix Assistant ToolWindow.
    /// Registered under Tools > AI Assistant > Warning Fix Assistant.
    /// </summary>
    internal sealed class OpenWarningTodoCommand
    {
        public static readonly Guid CommandSet = new Guid("5be5d683-76a1-4d14-b6bb-e3aff99d83a9");
        public const int CommandId = 0x0108;

        private readonly AsyncPackage _package;

        private OpenWarningTodoCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static OpenWarningTodoCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new OpenWarningTodoCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await OpenWindowAsync();
            });
        }

        public async Task OpenWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(
                typeof(WarningTodoToolWindow), 0, true,
                _package.DisposalToken);

            if (window?.Frame is IVsWindowFrame frame)
            {
                frame.Show();
            }
        }

        public async Task OpenAndAnalyzeAsync(System.Collections.Generic.List<WarningTodoItem> warnings, int limit)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(
                typeof(WarningTodoToolWindow), 0, true,
                _package.DisposalToken) as WarningTodoToolWindow;

            if (window?.Frame is IVsWindowFrame frame)
            {
                frame.Show();
            }

            if (window?.Panel != null)
            {
                window.Panel.InjectWarningsAndAnalyze(warnings, limit);
            }
        }
    }
}
