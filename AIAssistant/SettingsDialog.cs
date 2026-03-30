using System;
using System.Drawing;
using System.Windows.Forms;

namespace AIAssistant
{
    /// <summary>
    /// Settings dialog for LLM server configuration.
    /// Dark-themed WinForms dialog with Save/Load/Reset.
    /// </summary>
    public class SettingsDialog : Form
    {
        // Dark theme colors
        private static readonly Color BG = Color.FromArgb(30, 30, 30);
        private static readonly Color BG2 = Color.FromArgb(37, 37, 38);
        private static readonly Color FG = Color.FromArgb(212, 212, 212);
        private static readonly Color ACCENT = Color.FromArgb(14, 99, 156);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 60);
        private static readonly Color SUCCESS = Color.FromArgb(106, 153, 85);
        private static readonly Color WARNING = Color.FromArgb(220, 220, 170);

        private TextBox txtApiUrl;
        private TextBox txtModel;
        private NumericUpDown numMaxTokens;
        private NumericUpDown numTemperature;
        private NumericUpDown numTimeout;
        private CheckBox chkShowThinking;
        private NumericUpDown numMaxThinkingLength;
        private Label lblStatus;

        public SettingsDialog()
        {
            InitializeUI();
            LoadConfigToUI();
        }

        private void InitializeUI()
        {
            this.Text = "AI Assistant Settings";
            this.Width = 560;
            this.Height = 560;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = BG;
            this.ForeColor = FG;
            this.Font = new Font("Segoe UI", 9.5f);

            int y = 20;
            int labelWidth = 160;
            int inputLeft = 180;
            int inputWidth = 340;

            // Title
            var titleLabel = CreateLabel("⚙️ LLM Server Configuration", 20, y, 500);
            titleLabel.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(79, 193, 255);
            this.Controls.Add(titleLabel);
            y += 40;

            // Separator
            var sep1 = CreateSeparator(20, y, 500);
            this.Controls.Add(sep1);
            y += 15;

            // API URL
            this.Controls.Add(CreateLabel("API URL:", 20, y + 3, labelWidth));
            txtApiUrl = CreateTextBox(inputLeft, y, inputWidth);
            this.Controls.Add(txtApiUrl);
            y += 35;

            // Model
            this.Controls.Add(CreateLabel("Model Name:", 20, y + 3, labelWidth));
            txtModel = CreateTextBox(inputLeft, y, inputWidth);
            this.Controls.Add(txtModel);
            y += 35;

            // Max Tokens
            this.Controls.Add(CreateLabel("Max Tokens:", 20, y + 3, labelWidth));
            numMaxTokens = CreateNumericUpDown(inputLeft, y, 150, 100, 10000, 100);
            this.Controls.Add(numMaxTokens);
            y += 35;

            // Temperature
            this.Controls.Add(CreateLabel("Temperature:", 20, y + 3, labelWidth));
            numTemperature = CreateNumericUpDown(inputLeft, y, 150, 0, 200, 5);
            var tempNote = CreateLabel("(×0.01 → actual value)", inputLeft + 160, y + 3, 180);
            tempNote.ForeColor = Color.FromArgb(157, 160, 166);
            tempNote.Font = new Font("Segoe UI", 8f);
            this.Controls.Add(tempNote);
            this.Controls.Add(numTemperature);
            y += 35;

            // Timeout
            this.Controls.Add(CreateLabel("Timeout (sec):", 20, y + 3, labelWidth));
            numTimeout = CreateNumericUpDown(inputLeft, y, 150, 10, 3600, 10);
            this.Controls.Add(numTimeout);
            y += 35;

            // Separator
            var sep2 = CreateSeparator(20, y, 500);
            this.Controls.Add(sep2);
            y += 15;

            // Show Thinking
            chkShowThinking = new CheckBox
            {
                Text = "Show Thinking Blocks",
                Left = 20,
                Top = y,
                Width = 300,
                ForeColor = FG,
                FlatStyle = FlatStyle.Flat
            };
            this.Controls.Add(chkShowThinking);
            y += 30;

            // Max Thinking Length
            this.Controls.Add(CreateLabel("Max Thinking Length:", 20, y + 3, labelWidth));
            numMaxThinkingLength = CreateNumericUpDown(inputLeft, y, 150, 100, 50000, 500);
            this.Controls.Add(numMaxThinkingLength);
            y += 40;

            // Separator
            var sep3 = CreateSeparator(20, y, 500);
            this.Controls.Add(sep3);
            y += 15;

            // Status label
            lblStatus = CreateLabel("", 20, y, 300);
            lblStatus.Font = new Font("Segoe UI", 9f);
            this.Controls.Add(lblStatus);

            // Edit Prompts button (links to PromptConfigDialog)
            var btnEditPrompts = CreateButton("📝 Edit Prompts", 20, y - 3, 140, Color.FromArgb(79, 60, 120));
            btnEditPrompts.Click += (s, e) =>
            {
                var promptDialog = new PromptConfigDialog();
                promptDialog.ShowDialog();
            };
            this.Controls.Add(btnEditPrompts);

            // Buttons
            var btnSave = CreateButton("💾 Save", 270, y - 3, 80, SUCCESS);
            btnSave.Click += BtnSave_Click;
            this.Controls.Add(btnSave);

            var btnLoad = CreateButton("📂 Load", 360, y - 3, 80, ACCENT);
            btnLoad.Click += BtnLoad_Click;
            this.Controls.Add(btnLoad);

            var btnReset = CreateButton("🔄 Reset", 450, y - 3, 70, BORDER);
            btnReset.Click += BtnReset_Click;
            this.Controls.Add(btnReset);
        }

        private void LoadConfigToUI()
        {
            var config = AssistantConfig.Instance;
            txtApiUrl.Text = config.ApiUrl;
            txtModel.Text = config.Model;
            numMaxTokens.Value = Math.Min(Math.Max(config.MaxTokens, 100), 10000);
            numTemperature.Value = (int)(config.Temperature * 100);
            numTimeout.Value = Math.Min(Math.Max(config.TimeoutSeconds, 10), 3600);
            chkShowThinking.Checked = config.ShowThinking;
            numMaxThinkingLength.Value = Math.Min(Math.Max(config.MaxThinkingLength, 100), 50000);
        }

        private void SaveUIToConfig()
        {
            var config = AssistantConfig.Instance;
            config.ApiUrl = txtApiUrl.Text.Trim();
            config.Model = txtModel.Text.Trim();
            config.MaxTokens = (int)numMaxTokens.Value;
            config.Temperature = (double)numTemperature.Value / 100.0;
            config.TimeoutSeconds = (int)numTimeout.Value;
            config.ShowThinking = chkShowThinking.Checked;
            config.MaxThinkingLength = (int)numMaxThinkingLength.Value;
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            SaveUIToConfig();
            AssistantConfig.Instance.Save();
            lblStatus.Text = "✅ Settings saved!";
            lblStatus.ForeColor = SUCCESS;
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            AssistantConfig.Load();
            LoadConfigToUI();
            lblStatus.Text = "📂 Settings loaded.";
            lblStatus.ForeColor = Color.FromArgb(79, 193, 255);
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            AssistantConfig.Instance.ResetToDefaults();
            LoadConfigToUI();
            lblStatus.Text = "🔄 Reset to defaults.";
            lblStatus.ForeColor = WARNING;
        }

        #region UI Helpers

        private Label CreateLabel(string text, int x, int y, int width)
        {
            return new Label
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 22,
                ForeColor = FG,
                AutoSize = false
            };
        }

        private TextBox CreateTextBox(int x, int y, int width)
        {
            return new TextBox
            {
                Left = x,
                Top = y,
                Width = width,
                Height = 24,
                BackColor = BG2,
                ForeColor = FG,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private NumericUpDown CreateNumericUpDown(int x, int y, int width, int min, int max, int increment)
        {
            return new NumericUpDown
            {
                Left = x,
                Top = y,
                Width = width,
                Minimum = min,
                Maximum = max,
                Increment = increment,
                BackColor = BG2,
                ForeColor = FG,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Button CreateButton(string text, int x, int y, int width, Color bgColor)
        {
            return new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = bgColor,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
        }

        private Panel CreateSeparator(int x, int y, int width)
        {
            return new Panel
            {
                Left = x,
                Top = y,
                Width = width,
                Height = 1,
                BackColor = BORDER
            };
        }

        #endregion
    }
}
