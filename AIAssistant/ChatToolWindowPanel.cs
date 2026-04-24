using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// Interactive Chat panel control hosted inside ChatToolWindow.
    /// Dark-themed WinForms UserControl with chat history, input, and context buttons.
    /// </summary>
    public class ChatToolWindowPanel : UserControl
    {
        // Dark theme
        private static readonly Color BG = Color.FromArgb(30, 30, 30);
        private static readonly Color BG2 = Color.FromArgb(37, 37, 38);
        private static readonly Color FG = Color.FromArgb(212, 212, 212);
        private static readonly Color USER_BG = Color.FromArgb(14, 99, 156);
        private static readonly Color ASSISTANT_BG = Color.FromArgb(45, 45, 48);
        private static readonly Color CODE_BG = Color.FromArgb(25, 25, 25);
        private static readonly Color ACCENT = Color.FromArgb(79, 193, 255);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 60);
        private static readonly Color SUCCESS = Color.FromArgb(106, 153, 85);

        private RichTextBox chatDisplay;
        private TextBox chatInput;
        private Button btnSend;
        private Button btnSendWithCode;
        private Button btnSendWithFile;
        private Button btnClear;
        private Button btnInsertCode;
        private Label lblTokens;
        private Panel statusBar;

        private List<KeyValuePair<string, string>> chatHistory = new List<KeyValuePair<string, string>>();
        private string lastAssistantResponse = "";
        
        // Tracks the original code that the user sent as context, used for CodeReviewDialog
        private string lastOriginalCodeContext = "";
        private string lastOriginalFileName = "";
        private EnvDTE.EditPoint lastOriginalStartPoint = null;
        private EnvDTE.EditPoint lastOriginalEndPoint = null;
        
        // Attached files for context
        private List<string> attachedFiles = new List<string>();
        private FlowLayoutPanel attachmentPanel;
        private Button btnApplyChanges;

        public ChatToolWindowPanel()
        {
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.BackColor = BG;
            this.Dock = DockStyle.Fill;

            // Header
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(37, 37, 38),
                Padding = new Padding(10, 8, 10, 8)
            };

            var headerLabel = new Label
            {
                Text = "🤖 AI Code Assistant",
                Dock = DockStyle.Left,
                ForeColor = ACCENT,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                AutoSize = true
            };

            lblTokens = new Label
            {
                Text = "Tokens: 0",
                Dock = DockStyle.Right,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight
            };

            btnClear = new Button
            {
                Text = "🗑️ Clear",
                Dock = DockStyle.Right,
                Width = 70,
                FlatStyle = FlatStyle.Flat,
                BackColor = BORDER,
                ForeColor = FG,
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += BtnClear_Click;

            header.Controls.Add(headerLabel);
            header.Controls.Add(lblTokens);
            header.Controls.Add(btnClear);

            // Chat display
            chatDisplay = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BG,
                ForeColor = FG,
                Font = new Font("Consolas", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Padding = new Padding(10)
            };

            // Input area
            var inputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 105,
                BackColor = BG2,
                Padding = new Padding(8)
            };

            // Context buttons row
            var contextRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = BG2,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            btnSendWithCode = CreateSmallButton("📋 Send with Selected Code", SUCCESS);
            btnSendWithCode.Click += BtnSendWithCode_Click;

            btnSendWithFile = CreateSmallButton("📄 Send with Active File", ACCENT);
            btnSendWithFile.Click += BtnSendWithFile_Click;

            btnInsertCode = CreateSmallButton("⬇️ Insert Last Code", Color.FromArgb(80, 80, 80));
            btnInsertCode.Click += BtnInsertCode_Click;

            btnApplyChanges = CreateSmallButton("✅ Review & Apply Changes", Color.FromArgb(106, 153, 85));
            btnApplyChanges.Click += BtnApplyChanges_Click;
            btnApplyChanges.Visible = false; // Hidden until AI responds with code

            contextRow.Controls.Add(btnSendWithCode);
            contextRow.Controls.Add(btnSendWithFile);
            contextRow.Controls.Add(btnInsertCode);
            contextRow.Controls.Add(btnApplyChanges);

            // Input + Send row
            var inputRow = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 5, 0, 0)
            };

            // Attachment panel (Chips for dropped files)
            attachmentPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 0, // Starts hidden
                BackColor = BG2,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true
            };

            chatInput = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = FG,
                Font = new Font("Segoe UI", 10f),
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = ScrollBars.Vertical,
                AllowDrop = true
            };
            chatInput.KeyDown += ChatInput_KeyDown;
            chatInput.DragEnter += ChatInput_DragEnter;
            chatInput.DragDrop += ChatInput_DragDrop;

            btnSend = new Button
            {
                Text = "Send ▶",
                Dock = DockStyle.Right,
                Width = 80,
                FlatStyle = FlatStyle.Flat,
                BackColor = ACCENT,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSend.FlatAppearance.BorderSize = 0;
            btnSend.Click += BtnSend_Click;

            inputRow.Controls.Add(chatInput);
            inputRow.Controls.Add(attachmentPanel);
            inputRow.Controls.Add(btnSend);

            inputPanel.Controls.Add(inputRow);
            inputPanel.Controls.Add(contextRow);

            // Assemble
            this.Controls.Add(chatDisplay);
            this.Controls.Add(inputPanel);
            this.Controls.Add(header);
        }

        #region Event Handlers

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Enter to send
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendMessage(false);
            }
        }

        private void ChatInput_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void ChatInput_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    foreach (var file in files)
                    {
                        if (!attachedFiles.Contains(file))
                        {
                            attachedFiles.Add(file);
                        }
                    }
                    UpdateAttachmentUI();
                }
            }
        }

        private void UpdateAttachmentUI()
        {
            attachmentPanel.Controls.Clear();
            if (attachedFiles.Count == 0)
            {
                attachmentPanel.Height = 0;
                return;
            }

            attachmentPanel.Height = 28;
            foreach (var file in attachedFiles)
            {
                string fileName = System.IO.Path.GetFileName(file);
                
                var chip = new Panel
                {
                    Height = 22,
                    AutoSize = true,
                    BackColor = ACCENT,
                    Margin = new Padding(0, 0, 5, 2),
                    Padding = new Padding(5, 2, 5, 2)
                };

                var lbl = new Label
                {
                    Text = "📎 " + fileName,
                    AutoSize = true,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8.5f),
                    Dock = DockStyle.Left,
                    TextAlign = ContentAlignment.MiddleLeft
                };

                var btnClose = new Button
                {
                    Text = "✕",
                    Width = 20,
                    Height = 20,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White,
                    BackColor = ACCENT,
                    Dock = DockStyle.Right,
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                    Tag = file
                };
                btnClose.FlatAppearance.BorderSize = 0;
                btnClose.Click += (s, e) =>
                {
                    attachedFiles.Remove(((Button)s).Tag.ToString());
                    UpdateAttachmentUI();
                };

                chip.Controls.Add(lbl);
                chip.Controls.Add(btnClose);
                attachmentPanel.Controls.Add(chip);
            }
        }

        private void BtnSend_Click(object sender, EventArgs e)
        {
            SendMessage(false);
        }

        private void BtnSendWithCode_Click(object sender, EventArgs e)
        {
            SendMessage(true);
        }

        private void BtnSendWithFile_Click(object sender, EventArgs e)
        {
            SendMessageWithFile();
        }

        private void BtnInsertCode_Click(object sender, EventArgs e)
        {
            InsertLastCodeBlock();
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            chatHistory.Clear();
            chatDisplay.Clear();
            lastAssistantResponse = "";
            UpdateTokenCount();
        }

        #endregion

        #region Chat Logic

        private void SendMessage(bool includeSelectedCode)
        {
            string userMessage = chatInput.Text.Trim();
            if (string.IsNullOrEmpty(userMessage) && attachedFiles.Count == 0) return;

            string displayMessage = userMessage;
            string contextMessage = userMessage;

            // Capture the original code context for later approval dialog
            string capturedOriginalCode = "";
            string capturedFileName = "";
            EnvDTE.EditPoint startPoint = null;
            EnvDTE.EditPoint endPoint = null;

            if (includeSelectedCode)
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    capturedOriginalCode = Helper.GetSelectedText();
                    capturedFileName = Helper.GetActiveFileName() ?? "";
                    
                    // Capture exact selection range to ensure reliable replacement later
                    if (Helper.DteInstance?.ActiveDocument?.Selection is EnvDTE.TextSelection textSel)
                    {
                        startPoint = textSel.TopPoint.CreateEditPoint();
                        endPoint = textSel.BottomPoint.CreateEditPoint();
                    }
                });
            }

            // Bundle attached files (also capture first file content as original context)
            if (attachedFiles.Count > 0)
            {
                foreach (string file in attachedFiles)
                {
                    try
                    {
                        string fileName = System.IO.Path.GetFileName(file);
                        string extension = System.IO.Path.GetExtension(file).TrimStart('.');
                        if (string.IsNullOrEmpty(extension)) extension = "text";
                        
                        string content = System.IO.File.ReadAllText(file);
                        contextMessage += $"\n\nAttached File ({fileName}):\n```{extension}\n{content}\n```\n";
                        
                        displayMessage += $"\n[📎 Attached: {fileName}]";

                        // Use first attached file as original context for approval
                        if (string.IsNullOrEmpty(capturedOriginalCode))
                        {
                            capturedOriginalCode = content;
                            capturedFileName = fileName;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendMessage("System", $"Failed to read attachment {file}: {ex.Message}", Color.FromArgb(241, 76, 76));
                    }
                }
                
                attachedFiles.Clear();
                UpdateAttachmentUI();
            }

            chatInput.Text = "";
            chatInput.Enabled = false;
            btnSend.Enabled = false;
            btnSend.Text = "⏳...";

            AppendMessage("You", displayMessage.Trim(), USER_BG);

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string response = await Helper.ChatWithContextAsync(contextMessage, chatHistory, includeSelectedCode);
                    lastAssistantResponse = response;

                    this.Invoke((Action)(() =>
                    {
                        // Save original context for the approval dialog
                        lastOriginalCodeContext = capturedOriginalCode;
                        lastOriginalFileName = capturedFileName;
                        lastOriginalStartPoint = startPoint;
                        lastOriginalEndPoint = endPoint;

                        AppendMessage("AI Assistant", response, ASSISTANT_BG);

                        // Auto-detect code blocks and show/hide the Apply button
                        bool hasCodeBlock = Regex.IsMatch(response, @"```\w*\s*\n[\s\S]*?```");
                        btnApplyChanges.Visible = hasCodeBlock;

                        // If we had original code context AND AI responded with code, notify user
                        if (hasCodeBlock && !string.IsNullOrEmpty(lastOriginalCodeContext))
                        {
                            AppendMessage("System", 
                                "💡 Code changes detected. Click [✅ Review & Apply Changes] to review and apply, or [⬇️ Insert Last Code] to insert at cursor.", 
                                Color.FromArgb(79, 193, 255));
                        }
                        else if (hasCodeBlock)
                        {
                            AppendMessage("System", 
                                "💡 Code block detected. Click [⬇️ Insert Last Code] to insert at cursor, or [✅ Review & Apply Changes] to review.", 
                                Color.FromArgb(79, 193, 255));
                        }

                        UpdateTokenCount();
                        chatInput.Enabled = true;
                        btnSend.Enabled = true;
                        btnSend.Text = "Send ▶";
                        chatInput.Focus();
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke((Action)(() =>
                    {
                        AppendMessage("Error", ex.Message, Color.FromArgb(241, 76, 76));
                        chatInput.Enabled = true;
                        btnSend.Enabled = true;
                        btnSend.Text = "Send ▶";
                    }));
                }
            });
        }

        private void SendMessageWithFile()
        {
            string userMessage = chatInput.Text.Trim();
            if (string.IsNullOrEmpty(userMessage))
            {
                userMessage = "Explain this file";
            }

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                string fileContent = Helper.GetActiveFileContent();
                string fileName = Helper.GetActiveFileName();

                if (!string.IsNullOrEmpty(fileContent))
                {
                    string fileMsg = $"{userMessage}\n\nActive file ({fileName}):\n```\n{fileContent}\n```";

                    chatInput.Text = "";
                    chatInput.Enabled = false;
                    btnSend.Enabled = false;
                    btnSend.Text = "⏳...";

                    AppendMessage("You", $"{userMessage} [with active file: {fileName}]", USER_BG);

                    chatHistory.Add(new KeyValuePair<string, string>("user", fileMsg));
                    var truncatedHistory = LlmClient.TruncateHistory(chatHistory, 8000);

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            string response = await LlmClient.ChatAsync(truncatedHistory);
                            chatHistory.Add(new KeyValuePair<string, string>("assistant", response));
                            lastAssistantResponse = response;

                            this.Invoke((Action)(() =>
                            {
                                AppendMessage("AI Assistant", response, ASSISTANT_BG);
                                UpdateTokenCount();
                                chatInput.Enabled = true;
                                btnSend.Enabled = true;
                                btnSend.Text = "Send ▶";
                                chatInput.Focus();
                            }));
                        }
                        catch (Exception ex)
                        {
                            this.Invoke((Action)(() =>
                            {
                                AppendMessage("Error", ex.Message, Color.FromArgb(241, 76, 76));
                                chatInput.Enabled = true;
                                btnSend.Enabled = true;
                                btnSend.Text = "Send ▶";
                            }));
                        }
                    });
                }
                else
                {
                    AppendMessage("System", "No active file found.", Color.FromArgb(220, 220, 170));
                }
            });
        }

        private void InsertLastCodeBlock()
        {
            if (string.IsNullOrEmpty(lastAssistantResponse)) return;

            // Extract code from code blocks
            var match = Regex.Match(lastAssistantResponse, @"```(?:\w+)?\s*\n?([\s\S]*?)```");
            string codeToInsert = match.Success ? match.Groups[1].Value.Trim() : lastAssistantResponse;

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Helper.InsertTextAtCursor(codeToInsert);
            });

            AppendMessage("System", "✅ Code inserted at cursor position.", SUCCESS);
        }

        /// <summary>
        /// Shows the CodeReviewDialog for the last AI response.
        /// If original code context exists, shows side-by-side diff.
        /// If accepted, replaces the selected text or inserts at cursor.
        /// </summary>
        private void BtnApplyChanges_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(lastAssistantResponse)) return;

            string originalCode = lastOriginalCodeContext;
            if (string.IsNullOrEmpty(originalCode))
            {
                // No original context — try to grab current selection
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    originalCode = Helper.GetSelectedText();
                    lastOriginalFileName = Helper.GetActiveFileName() ?? "";
                });

                if (string.IsNullOrEmpty(originalCode))
                {
                    originalCode = "(No original code — select code in editor first, or use 'Send with Selected Code')";
                }
            }

            string actionName = string.IsNullOrEmpty(lastOriginalFileName) 
                ? "Chat Code Changes" 
                : $"Changes to {lastOriginalFileName}";

            var dialog = new CodeReviewDialog(actionName, originalCode, lastAssistantResponse);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && dialog.Accepted)
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    
                    if (lastOriginalStartPoint != null && lastOriginalEndPoint != null)
                    {
                        // 1. We have exact coordinates from the original selection. Replace exactly what was selected!
                        try
                        {
                            lastOriginalStartPoint.ReplaceText(lastOriginalEndPoint, dialog.ProposedCode, 0);
                        }
                        catch
                        {
                            // Fallback if EditPoint became invalid (e.g. document closed)
                            Helper.InsertTextAtCursor(dialog.ProposedCode);
                        }
                    }
                    else if (Helper.DteInstance?.ActiveDocument == null || string.IsNullOrEmpty(lastOriginalFileName))
                    {
                        // 2. Either no file is open, or the chat wasn't initiated from a specific file selection.
                        // Let's create a new file for the generated code.
                        Helper.DteInstance?.ItemOperations.NewFile("General\\Text File");
                        var activeDoc = Helper.DteInstance?.ActiveDocument;
                        if (activeDoc?.Selection is EnvDTE.TextSelection textSel)
                        {
                            textSel.Insert(dialog.ProposedCode);
                            // Try to guess a filename if the first line is exactly e.g. // MyClass.cs (not perfect but helpful)
                            var match = Regex.Match(dialog.ProposedCode, @"^\s*//\s*([a-zA-Z0-9_\-\.]+\.(cs|js|py|html|css|txt|md|cpp|h|xml|json))");
                            if (match.Success)
                            {
                                AppendMessage("System", $"✅ Code applied to a new file. You can save it as '{match.Groups[1].Value}'.", SUCCESS);
                            }
                            else
                            {
                                AppendMessage("System", "✅ Code applied to a new file.", SUCCESS);
                            }
                        }
                    }
                    else
                    {
                        // 3. We are in an active document but no original selection was tracked.
                        // Just insert at cursor or replace current selection.
                        string selectedText = Helper.GetSelectedText();
                        if (!string.IsNullOrWhiteSpace(selectedText))
                        {
                            if (Helper.DteInstance?.ActiveDocument?.Selection is EnvDTE.TextSelection textSel)
                            {
                                textSel.Insert(dialog.ProposedCode);
                            }
                        }
                        else
                        {
                            Helper.InsertTextAtCursor(dialog.ProposedCode);
                        }
                        AppendMessage("System", "✅ Changes applied to active document.", SUCCESS);
                    }
                });

                btnApplyChanges.Visible = false;
            }
            else
            {
                AppendMessage("System", "❌ Changes rejected.", Color.FromArgb(241, 76, 76));
            }
        }

        #endregion

        #region Display Helpers

        private void AppendMessage(string sender, string content, Color senderColor)
        {
            chatDisplay.SelectionStart = chatDisplay.TextLength;

            // Timestamp
            chatDisplay.SelectionColor = Color.FromArgb(100, 100, 100);
            chatDisplay.SelectionFont = new Font("Segoe UI", 7.5f);
            chatDisplay.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

            // Sender label
            chatDisplay.SelectionColor = senderColor;
            chatDisplay.SelectionFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            chatDisplay.AppendText($"{sender}:\n");

            // Content with code block highlighting
            AppendFormattedContent(content);

            chatDisplay.AppendText("\n\n");
            chatDisplay.ScrollToCaret();
        }

        private void AppendFormattedContent(string content)
        {
            // Split by code blocks
            var parts = Regex.Split(content, @"(```\w*\n[\s\S]*?```)");

            foreach (var part in parts)
            {
                if (part.StartsWith("```"))
                {
                    // Code block
                    var codeMatch = Regex.Match(part, @"```(\w*)\n?([\s\S]*?)```");
                    if (codeMatch.Success)
                    {
                        string lang = codeMatch.Groups[1].Value;
                        string code = codeMatch.Groups[2].Value.TrimEnd();

                        chatDisplay.SelectionColor = Color.FromArgb(157, 160, 166);
                        chatDisplay.SelectionFont = new Font("Segoe UI", 8f);
                        chatDisplay.AppendText($"── {(string.IsNullOrEmpty(lang) ? "code" : lang)} ──\n");

                        chatDisplay.SelectionColor = Color.FromArgb(206, 145, 120);
                        chatDisplay.SelectionFont = new Font("Consolas", 9.5f);
                        chatDisplay.AppendText(code + "\n");

                        chatDisplay.SelectionColor = Color.FromArgb(157, 160, 166);
                        chatDisplay.SelectionFont = new Font("Segoe UI", 8f);
                        chatDisplay.AppendText("────────────\n");
                    }
                }
                else
                {
                    // Regular text
                    chatDisplay.SelectionColor = FG;
                    chatDisplay.SelectionFont = new Font("Segoe UI", 9.5f);
                    chatDisplay.AppendText(part);
                }
            }
        }

        private void UpdateTokenCount()
        {
            int totalTokens = 0;
            foreach (var msg in chatHistory)
            {
                totalTokens += LlmClient.EstimateTokens(msg.Value);
            }
            lblTokens.Text = $"Tokens: ~{totalTokens}";
            lblTokens.ForeColor = totalTokens > 6000
                ? Color.FromArgb(241, 76, 76)
                : totalTokens > 4000
                    ? Color.FromArgb(220, 220, 170)
                    : Color.FromArgb(157, 160, 166);
        }

        private Button CreateSmallButton(string text, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                Height = 26,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = bgColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 5, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        #endregion
    }
}
