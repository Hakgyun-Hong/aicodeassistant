using System;
using System.Drawing;
using System.Windows.Forms;

namespace AIAssistant
{
    /// <summary>
    /// Dialog for editing the system prompts used by each code action.
    /// Tabbed layout with one tab per action. Supports Save/Load/Reset.
    /// </summary>
    public class PromptConfigDialog : Form
    {
        private static readonly Color BG = Color.FromArgb(30, 30, 30);
        private static readonly Color BG2 = Color.FromArgb(37, 37, 38);
        private static readonly Color FG = Color.FromArgb(212, 212, 212);
        private static readonly Color ACCENT = Color.FromArgb(14, 99, 156);
        private static readonly Color SUCCESS = Color.FromArgb(106, 153, 85);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 60);
        private static readonly Color WARNING = Color.FromArgb(220, 220, 170);

        private TabControl tabControl;
        private TextBox txtReview;
        private TextBox txtExplain;
        private TextBox txtOptimize;
        private TextBox txtRefactor;
        private TextBox txtGenerate;
        private Label lblStatus;

        public PromptConfigDialog()
        {
            InitializeUI();
            LoadToUI();
        }

        private void InitializeUI()
        {
            this.Text = "AI Assistant — Prompt Editor";
            this.Width = 720;
            this.Height = 580;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(600, 450);
            this.BackColor = BG;
            this.ForeColor = FG;
            this.Font = new Font("Segoe UI", 9.5f);

            // Header
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = BG2,
                Padding = new Padding(15, 10, 15, 10)
            };

            var headerLabel = new Label
            {
                Text = "📝 System Prompt Editor",
                Dock = DockStyle.Left,
                ForeColor = Color.FromArgb(79, 193, 255),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                AutoSize = true
            };

            var headerHint = new Label
            {
                Text = "각 코드 액션의 LLM 시스템 프롬프트를 편집합니다",
                Dock = DockStyle.Right,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight
            };

            header.Controls.Add(headerLabel);
            header.Controls.Add(headerHint);

            // Tab control
            tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f)
            };

            // Apply dark theme to tabs (limited in WinForms but we can style the text boxes)
            txtReview = CreatePromptTab("🔍 Review", "Review Code");
            txtExplain = CreatePromptTab("📖 Explain", "Explain Code");
            txtOptimize = CreatePromptTab("⚡ Optimize", "Optimize Code");
            txtRefactor = CreatePromptTab("🔧 Refactor", "Refactor Code");
            txtGenerate = CreatePromptTab("✨ Generate", "Generate Code");

            // Bottom button bar
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = BG2,
                Padding = new Padding(12, 10, 12, 10)
            };

            lblStatus = new Label
            {
                Text = "",
                Dock = DockStyle.Left,
                ForeColor = FG,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                BackColor = BG2,
                WrapContents = false
            };

            var btnSave = CreateButton("💾 Save", SUCCESS);
            btnSave.Click += (s, e) =>
            {
                SaveFromUI();
                PromptConfig.Instance.Save();
                lblStatus.Text = "✅ Prompts saved!";
                lblStatus.ForeColor = SUCCESS;
            };

            var btnLoad = CreateButton("📂 Load", ACCENT);
            btnLoad.Click += (s, e) =>
            {
                PromptConfig.Load();
                LoadToUI();
                lblStatus.Text = "📂 Prompts loaded.";
                lblStatus.ForeColor = Color.FromArgb(79, 193, 255);
            };

            var btnReset = CreateButton("🔄 Reset All", BORDER);
            btnReset.Click += (s, e) =>
            {
                var result = MessageBox.Show(
                    "모든 프롬프트를 기본값으로 초기화하시겠습니까?\nReset all prompts to defaults?",
                    "Reset Prompts",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result == DialogResult.Yes)
                {
                    PromptConfig.Instance.ResetToDefaults();
                    LoadToUI();
                    lblStatus.Text = "🔄 Reset to defaults.";
                    lblStatus.ForeColor = WARNING;
                }
            };

            btnPanel.Controls.Add(btnSave);
            btnPanel.Controls.Add(btnLoad);
            btnPanel.Controls.Add(btnReset);

            footer.Controls.Add(lblStatus);
            footer.Controls.Add(btnPanel);

            // Assemble
            this.Controls.Add(tabControl);
            this.Controls.Add(footer);
            this.Controls.Add(header);
        }

        private TextBox CreatePromptTab(string tabTitle, string actionName)
        {
            var tabPage = new TabPage(tabTitle)
            {
                BackColor = BG,
                Padding = new Padding(10)
            };

            var infoLabel = new Label
            {
                Text = $"System prompt for \"{actionName}\" — 한글 입력 가능합니다",
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 8.5f)
            };

            var textBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = FG,
                Font = new Font("Consolas", 10.5f),
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                AcceptsReturn = true,
                AcceptsTab = true
            };

            // Character count label
            var charCount = new Label
            {
                Text = "0 chars",
                Dock = DockStyle.Bottom,
                Height = 20,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 7.5f),
                TextAlign = ContentAlignment.MiddleRight
            };
            textBox.TextChanged += (s, e) =>
            {
                charCount.Text = $"{textBox.Text.Length} chars";
            };

            tabPage.Controls.Add(textBox);
            tabPage.Controls.Add(charCount);
            tabPage.Controls.Add(infoLabel);

            tabControl.TabPages.Add(tabPage);
            return textBox;
        }

        private void LoadToUI()
        {
            var pc = PromptConfig.Instance;
            txtReview.Text = pc.ReviewPrompt ?? "";
            txtExplain.Text = pc.ExplainPrompt ?? "";
            txtOptimize.Text = pc.OptimizePrompt ?? "";
            txtRefactor.Text = pc.RefactorPrompt ?? "";
            txtGenerate.Text = pc.GeneratePrompt ?? "";
        }

        private void SaveFromUI()
        {
            var pc = PromptConfig.Instance;
            pc.ReviewPrompt = txtReview.Text;
            pc.ExplainPrompt = txtExplain.Text;
            pc.OptimizePrompt = txtOptimize.Text;
            pc.RefactorPrompt = txtRefactor.Text;
            pc.GeneratePrompt = txtGenerate.Text;
        }

        private Button CreateButton(string text, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                Height = 30,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = bgColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                Margin = new Padding(5, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }
    }
}
