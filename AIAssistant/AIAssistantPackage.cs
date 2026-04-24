using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace AIAssistant
{
    /// <summary>
    /// AI Assistant Package — the main VS extension entry point.
    /// Registers all commands, tool windows, and loads configuration.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(AIAssistantPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [ProvideToolWindow(typeof(WarningTodoToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    public sealed class AIAssistantPackage : AsyncPackage
    {
        public const string PackageGuidString = "72094a93-38fb-4936-af38-b832374315eb";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Initialize helper (DTE, Output Window)
            await Helper.InitializeAsync(this);

            // Load config
            var _ = AssistantConfig.Instance;

            // Initialize all commands
            await ReviewCodeCommand.InitializeAsync(this);
            await ExplainCodeCommand.InitializeAsync(this);
            await OptimizeCodeCommand.InitializeAsync(this);
            await RefactorCodeCommand.InitializeAsync(this);
            await GenerateCodeCommand.InitializeAsync(this);
            await OpenChatCommand.InitializeAsync(this);
            await SettingsCommand.InitializeAsync(this);
            await OpenWarningTodoCommand.InitializeAsync(this);
        }
    }
}
