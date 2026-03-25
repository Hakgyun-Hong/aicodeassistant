using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AIAssistant
{
    /// <summary>
    /// Code change review/approval dialog.
    /// Shows original vs proposed code side-by-side with Accept/Reject buttons.
    /// Similar to Claude Code or Antigravity's diff approval UX.
    /// </summary>
    public class CodeReviewDialog : Form
    {
        // Dark theme
        private static readonly Color BG = Color.FromArgb(30, 30, 30);
        private static readonly Color BG2 = Color.FromArgb(37, 37, 38);
        private static readonly Color FG = Color.FromArgb(212, 212, 212);
        private static readonly Color ACCENT = Color.FromArgb(79, 193, 255);
        private static readonly Color SUCCESS = Color.FromArgb(106, 153, 85);
        private static readonly Color DANGER = Color.FromArgb(241, 76, 76);
        private static readonly Color ADD_BG = Color.FromArgb(35, 60, 35);
        private static readonly Color DEL_BG = Color.FromArgb(60, 30, 30);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 60);

        private RichTextBox txtOriginal;
        private RichTextBox txtProposed;
        private RichTextBox txtExplanation;

        /// <summary>
        /// The proposed code (extracted from code blocks) that will be applied if accepted.
        /// </summary>
        public string ProposedCode { get; private set; }

        /// <summary>
        /// True if the user accepted the changes.
        /// </summary>
        public bool Accepted { get; private set; }

        /// <summary>
        /// Creates the review dialog.
        /// </summary>
        /// <param name="actionName">The action name (e.g., "Optimize Code")</param>
        /// <param name="originalCode">The original selected code</param>
        /// <param name="fullResponse">Full LLM response (may contain code blocks + explanations)</param>
        public CodeReviewDialog(string actionName, string originalCode, string fullResponse)
        {
            Accepted = false;

            // Extract code block from response
            ProposedCode = ExtractCodeBlock(fullResponse);
            string explanation = ExtractExplanation(fullResponse);

            InitializeUI(actionName, originalCode, ProposedCode, explanation);
        }

        private void InitializeUI(string actionName, string original, string proposed, string explanation)
        {
            this.Text = $"AI Assistant — {actionName} Review";
            this.Width = 1100;
            this.Height = 700;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(800, 500);
            this.BackColor = BG;
            this.ForeColor = FG;
            this.Font = new Font("Segoe UI", 9.5f);

            // ═══ Top: Header ═══
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = BG2,
                Padding = new Padding(15, 10, 15, 10)
            };

            var headerLabel = new Label
            {
                Text = $"📋 {actionName} — Review Changes",
                Dock = DockStyle.Left,
                ForeColor = ACCENT,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                AutoSize = true
            };

            var headerHint = new Label
            {
                Text = "Review the proposed changes below. Accept to apply, Reject to discard.",
                Dock = DockStyle.Right,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight
            };

            headerPanel.Controls.Add(headerLabel);
            headerPanel.Controls.Add(headerHint);

            // ═══ Bottom: Buttons ═══
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 55,
                BackColor = BG2,
                Padding = new Padding(15, 10, 15, 10)
            };

            var btnAccept = new Button
            {
                Text = "✅ Accept Changes",
                Width = 160,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = SUCCESS,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right
            };
            btnAccept.FlatAppearance.BorderSize = 0;
            btnAccept.Click += (s, e) =>
            {
                Accepted = true;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            var btnReject = new Button
            {
                Text = "❌ Reject",
                Width = 100,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = DANGER,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right
            };
            btnReject.FlatAppearance.BorderSize = 0;
            btnReject.Click += (s, e) =>
            {
                Accepted = false;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            var btnCopyProposed = new Button
            {
                Text = "📋 Copy Proposed",
                Width = 130,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = BORDER,
                ForeColor = FG,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right
            };
            btnCopyProposed.FlatAppearance.BorderSize = 0;
            btnCopyProposed.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(proposed))
                {
                    Clipboard.SetText(proposed);
                }
            };

            // Use FlowLayoutPanel for right-aligned buttons
            var btnLayout = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                BackColor = BG2,
                WrapContents = false
            };
            btnLayout.Controls.Add(btnAccept);
            btnLayout.Controls.Add(btnReject);
            btnLayout.Controls.Add(btnCopyProposed);

            // Stats label
            int addedLines = proposed.Split('\n').Length;
            int removedLines = original.Split('\n').Length;
            var lblStats = new Label
            {
                Text = $"Original: {removedLines} lines  →  Proposed: {addedLines} lines",
                Dock = DockStyle.Left,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            buttonPanel.Controls.Add(btnLayout);
            buttonPanel.Controls.Add(lblStats);

            // ═══ Middle: Code panels ═══
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = BORDER,
                SplitterDistance = 400,
                SplitterWidth = 4
            };

            // Top half: side-by-side code
            var codeSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = BORDER,
                SplitterWidth = 4
            };

            // Left: Original
            var originalPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            var originalHeader = new Label
            {
                Text = "🔴 Original Code",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = DEL_BG,
                ForeColor = Color.FromArgb(241, 150, 150),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(8, 5, 0, 0)
            };
            txtOriginal = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BG,
                ForeColor = FG,
                Font = new Font("Consolas", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false,
                Text = original
            };
            originalPanel.Controls.Add(txtOriginal);
            originalPanel.Controls.Add(originalHeader);

            // Right: Proposed
            var proposedPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            var proposedHeader = new Label
            {
                Text = "🟢 Proposed Code",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = ADD_BG,
                ForeColor = Color.FromArgb(150, 220, 150),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(8, 5, 0, 0)
            };
            txtProposed = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BG,
                ForeColor = FG,
                Font = new Font("Consolas", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false,
                Text = proposed
            };
            proposedPanel.Controls.Add(txtProposed);
            proposedPanel.Controls.Add(proposedHeader);

            codeSplit.Panel1.Controls.Add(originalPanel);
            codeSplit.Panel2.Controls.Add(proposedPanel);

            // Bottom: Explanation
            var explanationPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG };
            var explanationHeader = new Label
            {
                Text = "💡 Explanation",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(40, 40, 50),
                ForeColor = ACCENT,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(8, 5, 0, 0)
            };
            txtExplanation = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BG,
                ForeColor = FG,
                Font = new Font("Segoe UI", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = true,
                Text = string.IsNullOrWhiteSpace(explanation) ? "(No explanation provided)" : explanation
            };
            explanationPanel.Controls.Add(txtExplanation);
            explanationPanel.Controls.Add(explanationHeader);

            mainSplit.Panel1.Controls.Add(codeSplit);
            mainSplit.Panel2.Controls.Add(explanationPanel);

            // Assemble everything
            this.Controls.Add(mainSplit);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(headerPanel);

            // Keyboard shortcuts
            this.KeyPreview = true;
            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && e.Control)
                {
                    Accepted = true;
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    Accepted = false;
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
            };
        }

        #region Text Extraction Helpers

        /// <summary>
        /// Extracts the first code block from the LLM response.
        /// </summary>
        private static string ExtractCodeBlock(string response)
        {
            if (string.IsNullOrEmpty(response)) return response;

            var match = Regex.Match(response, @"```(?:\w*)\s*\n([\s\S]*?)```");
            if (match.Success)
            {
                return match.Groups[1].Value.TrimEnd();
            }

            // No code block found — return the full response as-is
            return response.Trim();
        }

        /// <summary>
        /// Extracts the explanation text (everything outside code blocks).
        /// </summary>
        private static string ExtractExplanation(string response)
        {
            if (string.IsNullOrEmpty(response)) return "";

            // Remove all code blocks, keep the rest
            string explanation = Regex.Replace(response, @"```(?:\w*)\s*\n[\s\S]*?```", "").Trim();
            return explanation;
        }

        #endregion
    }
}
