using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace AIAssistant
{
    internal sealed class Helper
    {
        internal static IVsOutputWindowPane OutputWindow { get; private set; }
        internal static DTE2 DteInstance { get; private set; }

        internal static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            DteInstance = DteInstance ?? await package.GetServiceAsync(typeof(DTE)) as DTE2;
            Assumes.Present(DteInstance);
            OutputWindow = OutputWindow ?? await package.GetServiceAsync(typeof(SVsGeneralOutputWindowPane)) as IVsOutputWindowPane;
            Assumes.Present(OutputWindow);
            OutputWindow.SetName("AI Assistant");

            // Load config on startup
            var _ = AssistantConfig.Instance;
        }

        #region Code Action Entry Points

        internal static async Task ReviewSelectedCodeAsync()
        {
            var prompt = @"You are an expert code reviewer. Review the following code thoroughly.
Provide:
1. Potential bugs or issues
2. Code quality improvements
3. Performance suggestions
4. Security concerns (if any)
5. Best practice violations

Be concise but thorough. Use bullet points.";
            await ExecuteCodeActionAsync(prompt, "Review Code");
        }

        internal static async Task ExplainSelectedCodeAsync()
        {
            var prompt = @"You are an expert programmer. Explain the following code in detail.
Provide:
1. Overall purpose of the code
2. Step-by-step breakdown of the logic
3. Key data structures and algorithms used
4. Any notable patterns or techniques

Be clear and educational.";
            await ExecuteCodeActionAsync(prompt, "Explain Code");
        }

        internal static async Task OptimizeSelectedCodeAsync()
        {
            var prompt = @"You are a performance optimization expert. Optimize the following code.
Provide:
1. The optimized version of the code
2. Explanation of each optimization
3. Expected performance improvement

Return the complete optimized code in a code block, followed by explanations.";
            await ExecuteCodeActionWithApprovalAsync(prompt, "Optimize Code");
        }

        internal static async Task RefactorSelectedCodeAsync()
        {
            var prompt = @"You are a refactoring expert. Refactor the following code for better readability, maintainability, and adherence to SOLID principles.
Provide:
1. The refactored code
2. Explanation of refactoring decisions
3. Design patterns applied (if any)

Return the complete refactored code in a code block, followed by explanations.";
            await ExecuteCodeActionWithApprovalAsync(prompt, "Refactor Code");
        }

        internal static async Task GenerateCodeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string description = Microsoft.VisualStudio.Shell.Interop.InputBox.Show(
                "Generate Code",
                "Describe the code you want to generate:",
                "");

            if (string.IsNullOrWhiteSpace(description))
            {
                DteInstance.StatusBar.Text = "Code generation cancelled.";
                return;
            }

            var config = AssistantConfig.Instance;
            string activeFile = GetActiveFileName();
            string fileContext = "";
            if (!string.IsNullOrEmpty(activeFile))
            {
                fileContext = $"\nActive file: {activeFile}";
            }

            var systemPrompt = @"You are an expert code generator. Generate clean, well-documented code based on the user's description.
Follow best practices for the language. Include comments. Return only the code in a code block.";

            var userContent = $"Generate the following code:{fileContext}\n\nDescription: {description}";

            ShowInOutputWindow($"🔄 Generating code: {description}...\n");
            DteInstance.StatusBar.Text = "AI Assistant: Generating code...";

            var response = await LlmClient.AskAsync(systemPrompt, userContent);

            // Show approval dialog for generated code
            var dialog = new CodeReviewDialog("Generate Code", $"// Description: {description}", response);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && dialog.Accepted)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                InsertTextAtCursor(dialog.ProposedCode);
                DteInstance.StatusBar.Text = "AI Assistant: Generated code inserted.";
                ShowInOutputWindow($"✅ Generated code accepted and inserted.\n");
            }
            else
            {
                DteInstance.StatusBar.Text = "AI Assistant: Code generation rejected.";
                ShowInOutputWindow($"❌ Generated code rejected by user.\n");
            }
        }

        #endregion

        #region Core Execution (with Approval)

        /// <summary>
        /// Execute a code action that shows results in a styled HTML viewer (Review, Explain).
        /// </summary>
        private static async Task ExecuteCodeActionAsync(string systemPrompt, string actionName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var selectedText = GetSelectedText();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                DteInstance.StatusBar.Text = "No code selected. Please select code first.";
                return;
            }

            string fileName = GetActiveFileName();
            string fileInfo = !string.IsNullOrEmpty(fileName) ? $" (from {fileName})" : "";

            var userContent = $"```\n{selectedText}\n```";

            ShowInOutputWindow($"🔄 {actionName}{fileInfo}...\n");
            DteInstance.StatusBar.Text = $"AI Assistant: {actionName} in progress...";

            var response = await LlmClient.AskAsync(systemPrompt, userContent);

            // Show in the HTML response viewer
            string promptInfo = $"{actionName}{fileInfo}\n\n{selectedText}";
            var viewer = new ResponseViewDialog(actionName, promptInfo, response);
            viewer.Show();

            DteInstance.StatusBar.Text = $"AI Assistant: {actionName} completed.";
        }

        /// <summary>
        /// Execute a code action that shows an approval dialog before applying changes (Optimize, Refactor).
        /// </summary>
        private static async Task ExecuteCodeActionWithApprovalAsync(string systemPrompt, string actionName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var selectedText = GetSelectedText();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                DteInstance.StatusBar.Text = "No code selected. Please select code first.";
                return;
            }

            string fileName = GetActiveFileName();
            string fileInfo = !string.IsNullOrEmpty(fileName) ? $" (from {fileName})" : "";

            var userContent = $"```\n{selectedText}\n```";

            ShowInOutputWindow($"🔄 {actionName}{fileInfo}...\n");
            DteInstance.StatusBar.Text = $"AI Assistant: {actionName} in progress...";

            var response = await LlmClient.AskAsync(systemPrompt, userContent);

            // Show the approval dialog
            var dialog = new CodeReviewDialog(actionName, selectedText, response);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && dialog.Accepted)
            {
                // Replace selected code with proposed code
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (DteInstance?.ActiveDocument?.Selection is EnvDTE.TextSelection textSelection)
                {
                    textSelection.Insert(dialog.ProposedCode);
                }
                DteInstance.StatusBar.Text = $"AI Assistant: {actionName} applied.";
                ShowInOutputWindow($"✅ {actionName} accepted and applied{fileInfo}.\n");
            }
            else
            {
                DteInstance.StatusBar.Text = $"AI Assistant: {actionName} rejected.";
                ShowInOutputWindow($"❌ {actionName} rejected by user{fileInfo}.\n");
            }
        }

        #region Chat with Context

        internal static async Task<string> ChatWithContextAsync(string userMessage, 
            List<KeyValuePair<string, string>> history, bool includeSelectedCode = false)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string contextMessage = userMessage;

            if (includeSelectedCode)
            {
                string selectedText = GetSelectedText();
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    string fileName = GetActiveFileName();
                    string fileInfo = !string.IsNullOrEmpty(fileName) ? $" (from {fileName})" : "";
                    contextMessage = $"{userMessage}\n\nSelected code{fileInfo}:\n```\n{selectedText}\n```";
                }
            }

            // Add system message if history is empty
            if (history.Count == 0 || history[0].Key != "system")
            {
                history.Insert(0, new KeyValuePair<string, string>("system",
                    @"You are an expert AI coding assistant integrated into Visual Studio. 
Help the developer with code questions, debugging, architecture, and best practices. 
When providing code, use proper code blocks with language identifiers. 
Be concise, accurate, and helpful. You can help with any programming language."));
            }

            history.Add(new KeyValuePair<string, string>("user", contextMessage));

            // Truncate history to stay within token limits
            var truncatedHistory = LlmClient.TruncateHistory(history, 8000);

            string response = await LlmClient.ChatAsync(truncatedHistory);

            history.Add(new KeyValuePair<string, string>("assistant", response));

            return response;
        }

        /// <summary>
        /// Get active file content for "Send with Active File" feature.
        /// </summary>
        internal static string GetActiveFileContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = DteInstance?.ActiveDocument;
                if (doc == null) return null;

                var textDoc = doc.Object("TextDocument") as TextDocument;
                if (textDoc == null) return null;

                var editPoint = textDoc.StartPoint.CreateEditPoint();
                return editPoint.GetText(textDoc.EndPoint);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region VS Helpers

        internal static string GetSelectedText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(DteInstance?.ActiveDocument?.Selection is TextSelection textSelection))
            {
                return string.Empty;
            }
            return textSelection.Text?.Trim();
        }

        internal static string GetActiveFileName()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return DteInstance?.ActiveDocument?.Name;
            }
            catch
            {
                return null;
            }
        }

        internal static void InsertTextAtCursor(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (DteInstance?.ActiveDocument?.Selection is TextSelection selection)
                {
                    selection.Insert(text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] Insert error: {ex.Message}");
            }
        }

        internal static void ShowInOutputWindow(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (OutputWindow != null)
            {
                OutputWindow.OutputStringThreadSafe(message);
                OutputWindow.OutputStringThreadSafe(Environment.NewLine +
                    "═════════════════════════════════════════" + Environment.NewLine);
                OutputWindow.Activate();
            }
        }

        #endregion
    }

    #region InputBox Helper (simple VS input dialog)

    namespace Microsoft.VisualStudio.Shell.Interop
    {
        internal static class InputBox
        {
            internal static string Show(string title, string prompt, string defaultValue)
            {
                using (var form = new System.Windows.Forms.Form())
                {
                    form.Text = title;
                    form.Width = 500;
                    form.Height = 180;
                    form.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                    form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                    form.MaximizeBox = false;
                    form.MinimizeBox = false;
                    form.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
                    form.ForeColor = System.Drawing.Color.FromArgb(212, 212, 212);

                    var label = new System.Windows.Forms.Label()
                    {
                        Text = prompt,
                        Left = 20,
                        Top = 20,
                        Width = 440,
                        ForeColor = System.Drawing.Color.FromArgb(212, 212, 212)
                    };

                    var textBox = new System.Windows.Forms.TextBox()
                    {
                        Text = defaultValue,
                        Left = 20,
                        Top = 50,
                        Width = 440,
                        BackColor = System.Drawing.Color.FromArgb(37, 37, 38),
                        ForeColor = System.Drawing.Color.FromArgb(212, 212, 212),
                        BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle
                    };

                    var okButton = new System.Windows.Forms.Button()
                    {
                        Text = "Generate",
                        Left = 300,
                        Top = 90,
                        Width = 75,
                        Height = 30,
                        DialogResult = System.Windows.Forms.DialogResult.OK,
                        FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                        BackColor = System.Drawing.Color.FromArgb(14, 99, 156),
                        ForeColor = System.Drawing.Color.White
                    };

                    var cancelButton = new System.Windows.Forms.Button()
                    {
                        Text = "Cancel",
                        Left = 385,
                        Top = 90,
                        Width = 75,
                        Height = 30,
                        DialogResult = System.Windows.Forms.DialogResult.Cancel,
                        FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                        BackColor = System.Drawing.Color.FromArgb(60, 60, 60),
                        ForeColor = System.Drawing.Color.FromArgb(212, 212, 212)
                    };

                    form.Controls.AddRange(new System.Windows.Forms.Control[] { label, textBox, okButton, cancelButton });
                    form.AcceptButton = okButton;
                    form.CancelButton = cancelButton;

                    return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? textBox.Text : null;
                }
            }
        }
    }

    #endregion
}
