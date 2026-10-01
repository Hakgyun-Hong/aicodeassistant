using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AIAssistant
{
    /// <summary>
    /// Command to open the SAM Fix Assistant ToolWindow.
    /// Registered under Tools > AI Assistant > SAM Fix Assistant.
    /// </summary>
    internal sealed class OpenSamTodoCommand
    {
        // Use a different CommandSet GUID from the Warning command set.
        public static readonly Guid CommandSet = new Guid("5be5d683-76a1-4d14-b6bb-e3aff99d83a9");
        public const int CommandId = 0x0120;   // distinct from Warning (0x0108)

        private readonly AsyncPackage _package;

        private OpenSamTodoCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var menuCommandID = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, menuCommandID));
        }

        public static OpenSamTodoCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new OpenSamTodoCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () => await OpenWindowAsync());
        }

        public async Task OpenWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(
                typeof(SamTodoToolWindow), 0, true, _package.DisposalToken);

            if (window?.Frame is IVsWindowFrame frame)
                frame.Show();
        }
    }
}
