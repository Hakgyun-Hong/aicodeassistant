using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace AIAssistant
{
    internal sealed class RefactorCodeCommand
    {
        public const int CommandId = 0x0104;
        public static readonly Guid CommandSet = new Guid("5be5d683-76a1-4d14-b6bb-e3aff99d83a9");
        private readonly AsyncPackage package;

        private RefactorCodeCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static RefactorCodeCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RefactorCodeCommand(package, commandService);
        }

        private async void Execute(object sender, EventArgs e)
        {
            await Helper.RefactorSelectedCodeAsync();
        }
    }
}
