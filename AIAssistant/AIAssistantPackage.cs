using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using EnvDTE;
using EnvDTE80;

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
    [ProvideToolWindow(typeof(SamTodoToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    public sealed class AIAssistantPackage : AsyncPackage
    {
        public const string PackageGuidString = "72094a93-38fb-4936-af38-b832374315eb";

        private BuildEvents _buildEvents;

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
            await OpenSamTodoCommand.InitializeAsync(this);

            // Hook Build Events for automated warning analysis
            if (await this.GetServiceAsync(typeof(DTE)) is DTE2 dte)
            {
                _buildEvents = dte.Events.BuildEvents;
                _buildEvents.OnBuildDone += BuildEvents_OnBuildDone;
            }
        }

        private void BuildEvents_OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                var config = AssistantConfig.Instance;
                if (!config.EnableWarningFixAssistant) return;

                // Tiny delay to ensure DTE.ErrorList is fully populated before collecting
                await Task.Delay(500);

                var warnings = WarningCollector.CollectWarnings();
                if (warnings.Count == 0) return;

                if (config.AutoAnalyzeWarnings)
                {
                    await OpenWarningTodoCommand.Instance.OpenAndAnalyzeAsync(warnings, config.MaxWarningsToAnalyze);
                }
                else if (config.ShowWarningPromptOnBuild)
                {
                    var result = System.Windows.Forms.MessageBox.Show(
                        $"Build completed with {warnings.Count} warnings.\nDo you want AI Assistant to analyze them (up to {config.MaxWarningsToAnalyze})?",
                        "⚠️ AI Warning Fix Assistant",
                        System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Information);

                    if (result == System.Windows.Forms.DialogResult.Yes)
                    {
                        await OpenWarningTodoCommand.Instance.OpenAndAnalyzeAsync(warnings, config.MaxWarningsToAnalyze);
                    }
                }
            });
        }
    }
}
