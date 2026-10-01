using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AIAssistant
{
    /// <summary>
    /// Opens the WPF Auto Manual Generator tool window.
    /// Registered under: Tools > AI Assistant > 📖 WPF Auto Manual Generator
    /// CommandId: 0x0130
    /// </summary>
    internal sealed class OpenManualCommand
    {
        public static readonly Guid CommandSet = new Guid("5be5d683-76a1-4d14-b6bb-e3aff99d83a9");
        public const int CommandId = 0x0130;

        private readonly AsyncPackage _package;

        private OpenManualCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static OpenManualCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cs = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new OpenManualCommand(package, cs);
        }

        private void Execute(object sender, EventArgs e)
            => ThreadHelper.JoinableTaskFactory.RunAsync(async () => await OpenWindowAsync());

        public async Task OpenWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(
                typeof(ManualToolWindow), 0, true, _package.DisposalToken);
            if (window?.Frame is IVsWindowFrame frame)
                frame.Show();
        }
    }
}
