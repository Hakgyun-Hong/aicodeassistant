using System;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// WinForms panel for the Warning Fix Assistant ToolWindow.
    /// Left: DataGridView listing warnings. Right: Detail pane with diff and Accept/Reject.
    /// </summary>
    public class WarningTodoPanel : UserControl
    {
        // Dark theme colors (consistent with existing panels)
        private static readonly Color BG = Color.FromArgb(30, 30, 30);
        private static readonly Color BG2 = Color.FromArgb(37, 37, 38);
        private static readonly Color FG = Color.FromArgb(212, 212, 212);
        private static readonly Color ACCENT = Color.FromArgb(14, 99, 156);
        private static readonly Color SUCCESS = Color.FromArgb(106, 153, 85);
        private static readonly Color WARNING_CLR = Color.FromArgb(220, 220, 170);
        private static readonly Color DANGER = Color.FromArgb(241, 76, 76);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 60);

        private DataGridView gridView;
        private RichTextBox txtDetail;
        private RichTextBox txtDiff;
        private Label lblStatus;
        private Button btnAccept;
        private Button btnReject;
        private Button btnPreview;
        private Button btnReanalyze;

        // Track the last item we navigated to so single-click doesn't re-navigate
        // when the grid refreshes and re-selects the same row.
        private Guid _lastNavigatedItemId = Guid.Empty;

        private WarningTodoStore _store;
        private WarningAnalysisQueue _analysisQueue;

        public WarningTodoPanel()
        {
            _store = WarningTodoStore.Instance;
            _analysisQueue = new WarningAnalysisQueue(_store);
            _analysisQueue.ProgressChanged += OnProgressChanged;

            InitializeUI();
            BindStore();
        }

        private void InitializeUI()
        {
            this.BackColor = BG;
            this.ForeColor = FG;
            this.Font = new Font("Segoe UI", 9f);

            // ═══ Top Toolbar ═══
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = BG2,
                Padding = new Padding(6, 5, 6, 5),
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight
            };

            var btnScan = CreateToolButton("🔍 Scan Warnings", ACCENT);
            btnScan.Click += BtnScan_Click;

            var btnAnalyzeAll = CreateToolButton("⚡ Analyze All", Color.FromArgb(79, 60, 120));
            btnAnalyzeAll.Click += BtnAnalyzeAll_Click;

            var btnClear = CreateToolButton("🧹 Clear", BORDER);
            btnClear.Click += BtnClear_Click;

            var btnCancel = CreateToolButton("⏹ Cancel", Color.FromArgb(100, 50, 50));
            btnCancel.Click += (s, e) => _analysisQueue.Cancel();

            toolbar.Controls.Add(btnScan);
            toolbar.Controls.Add(btnAnalyzeAll);
            toolbar.Controls.Add(btnCancel);
            toolbar.Controls.Add(btnClear);

            // ═══ Bottom Status Bar ═══
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = BG2,
                ForeColor = Color.FromArgb(157, 160, 166),
                Font = new Font("Segoe UI", 8f),
                Text = "Ready. Click 'Scan Warnings' after a build.",
                Padding = new Padding(8, 4, 8, 4),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // ═══ Main Split Panel ═══
            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 3,
                BackColor = BORDER,
                SplitterDistance = 400
            };

            // ─── Left: DataGridView ───
            gridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = BG,
                ForeColor = FG,
                GridColor = BORDER,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Font = new Font("Segoe UI", 9f),
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BG,
                    ForeColor = FG,
                    SelectionBackColor = Color.FromArgb(38, 79, 120),
                    SelectionForeColor = Color.White
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BG2,
                    ForeColor = Color.FromArgb(79, 193, 255),
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                },
                EnableHeadersVisualStyles = false
            };

            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 90, DataPropertyName = "StatusText" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "Code", Width = 70, DataPropertyName = "Code" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "File", Width = 120, DataPropertyName = "ShortFilePath" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "Line", Width = 50, DataPropertyName = "Line" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Summary", HeaderText = "Summary", Width = 200, DataPropertyName = "LlmSummary" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Risk", HeaderText = "Risk", Width = 60, DataPropertyName = "RiskLevel" });

            gridView.SelectionChanged += GridView_SelectionChanged;
            gridView.CellDoubleClick += GridView_CellDoubleClick;

            splitContainer.Panel1.Controls.Add(gridView);
            splitContainer.Panel1.BackColor = BG;

            // ─── Right: Detail Pane ───
            var rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BG,
                Padding = new Padding(8)
            };

            // Detail text (warning info + explanation)
            txtDetail = new RichTextBox
            {
                Dock = DockStyle.Top,
                Height = 120,
                BackColor = BG2,
                ForeColor = FG,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            // Diff view
            var diffLabel = new Label
            {
                Text = "📝 Proposed Change",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Color.FromArgb(79, 193, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Padding = new Padding(0, 4, 0, 0)
            };

            txtDiff = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(26, 26, 26),
                ForeColor = FG,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };

            // Action buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = BG2,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 6, 4, 6),
                WrapContents = false
            };

            btnAccept = CreateActionButton("✅ Accept & Apply", SUCCESS);
            btnAccept.Click += BtnAccept_Click;

            btnReject = CreateActionButton("❌ Reject", DANGER);
            btnReject.Click += BtnReject_Click;

            btnPreview = CreateActionButton("🔎 Preview Diff", ACCENT);
            btnPreview.Click += BtnPreview_Click;

            btnReanalyze = CreateActionButton("🔄 Re-analyze", Color.FromArgb(60, 90, 60));
            btnReanalyze.Click += BtnReanalyze_Click;

            buttonPanel.Controls.Add(btnAccept);
            buttonPanel.Controls.Add(btnReject);
            buttonPanel.Controls.Add(btnPreview);
            buttonPanel.Controls.Add(btnReanalyze);

            rightPanel.Controls.Add(txtDiff);
            rightPanel.Controls.Add(diffLabel);
            rightPanel.Controls.Add(txtDetail);
            rightPanel.Controls.Add(buttonPanel);

            splitContainer.Panel2.Controls.Add(rightPanel);
            splitContainer.Panel2.BackColor = BG;

            // ═══ Assemble ═══
            this.Controls.Add(splitContainer);
            this.Controls.Add(lblStatus);
            this.Controls.Add(toolbar);
        }

        #region Event Handlers

        private void BtnScan_Click(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var warnings = WarningCollector.CollectWarnings();
                foreach (var w in warnings)
                {
                    _store.AddOrUpdate(w);
                }

                RefreshGrid();
                lblStatus.Text = $"Scanned {warnings.Count} warnings from Error List. {_store.GetStatusSummary()}";
            });
        }

        private async void BtnAnalyzeAll_Click(object sender, EventArgs e)
        {
            var pending = _store.Items
                .Where(x => x.Status == WarningFixStatus.PendingAnalysis)
                .ToList();

            if (pending.Count == 0)
            {
                lblStatus.Text = "No pending warnings to analyze.";
                return;
            }

            lblStatus.Text = $"Analyzing {pending.Count} warnings...";

            foreach (var item in pending)
            {
                _analysisQueue.Enqueue(item);
            }

            await _analysisQueue.ProcessAllAsync();

            this.Invoke((Action)(() =>
            {
                RefreshGrid();
                lblStatus.Text = _store.GetStatusSummary();
            }));
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _store.Clear();
            RefreshGrid();
            txtDetail.Clear();
            txtDiff.Clear();
            lblStatus.Text = "Cleared.";
        }

        private void BtnAccept_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null || item.Status != WarningFixStatus.Suggested) return;

            string error = WarningFixApplier.Apply(item);
            if (error == null)
            {
                lblStatus.Text = $"✅ Fix applied to {item.ShortFilePath}. Backup saved as .warningfix.bak";
                _store.NotifyChanged();

                // Navigate to the fix location so the user can immediately review
                // the change. Reset the last-navigated ID so NavigateToItem runs
                // even though the same item is still selected.
                _lastNavigatedItemId = Guid.Empty;
                NavigateToItem(item);
            }
            else
            {
                lblStatus.Text = $"⚠️ {error}";
            }

            RefreshGrid();
            ShowDetail(item);
        }

        private void BtnReject_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;

            _store.Reject(item);
            RefreshGrid();
            ShowDetail(item);
            lblStatus.Text = $"❌ Rejected fix for {item.Code} in {item.ShortFilePath}";
        }

        private async void BtnReanalyze_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;

            // Disable button to prevent double-clicks during re-analysis.
            btnReanalyze.Enabled = false;
            lblStatus.Text = $"🔄 Re-analyzing {item.Code} in {item.ShortFilePath}...";

            await _analysisQueue.ReanalyzeAsync(item);

            // Marshal back to UI thread (ReanalyzeAsync completes on background thread).
            try
            {
                this.Invoke((Action)(() =>
                {
                    RefreshGrid();
                    ShowDetail(item);
                    lblStatus.Text = $"Re-analysis complete: {item.StatusText}  {item.Code} in {item.ShortFilePath}";
                }));
            }
            catch { }
        }

        private void BtnPreview_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null || item.Status != WarningFixStatus.Suggested) return;

            string original = item.OriginalSnippet ?? "(no original snippet)";
            string proposed = item.ProposedSnippet ?? "(no proposed snippet)";
            string explanation = $"{item.Code}: {item.Message}\n\n{item.LlmExplanation}";

            var dialog = new CodeReviewDialog(
                $"Warning Fix: {item.Code}",
                original,
                $"```\n{proposed}\n```\n\n{explanation}");

            dialog.ShowDialog();
        }

        private void GridView_SelectionChanged(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;

            ShowDetail(item);

            // Navigate VS editor to the warning location on single-click,
            // but only when a different item is selected (avoid re-navigating
            // during grid refresh that preserves the current selection).
            if (item.Id != _lastNavigatedItemId)
            {
                _lastNavigatedItemId = item.Id;
                NavigateToItem(item);
            }
        }

        private void GridView_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var item = GetSelectedItem();
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;

            // Double-click: force re-navigation even if it's the same item
            _lastNavigatedItemId = item.Id;
            NavigateToItem(item);
        }

        /// <summary>
        /// Open <paramref name="item"/>'s file in Visual Studio and move the cursor
        /// to the exact line and column reported by the build error list.
        /// </summary>
        private void NavigateToItem(WarningTodoItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = Helper.DteInstance;
                    if (dte == null) return;

                    // Open the file (no-op if already open)
                    dte.ItemOperations.OpenFile(item.FilePath);

                    if (dte.ActiveDocument?.Selection is EnvDTE.TextSelection textSel)
                    {
                        // Move to line first
                        textSel.GotoLine(item.Line, Select: false);

                        // Then move to the specific column within that line
                        if (item.Column > 1)
                        {
                            try
                            {
                                // MoveToLineAndOffset is 1-based
                                var editPoint = (dte.ActiveDocument.Object("TextDocument") as EnvDTE.TextDocument)
                                                    ?.CreateEditPoint();
                                if (editPoint != null)
                                {
                                    editPoint.MoveToLineAndOffset(item.Line, item.Column);
                                    textSel.MoveToPoint(editPoint, Extend: false);
                                }
                            }
                            catch
                            {
                                // Column navigation failed — line position is already set, that's fine.
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private void OnProgressChanged(int processed, int total)
        {
            try
            {
                this.Invoke((Action)(() =>
                {
                    lblStatus.Text = $"Analyzing... {processed}/{total} completed. {_store.GetStatusSummary()}";
                    RefreshGrid();
                }));
            }
            catch { }
        }

        public void InjectWarningsAndAnalyze(System.Collections.Generic.List<WarningTodoItem> warnings, int limit)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => InjectWarningsAndAnalyze(warnings, limit)));
                return;
            }

            int count = Math.Min(warnings.Count, limit);
            for (int i = 0; i < count; i++)
            {
                _store.AddOrUpdate(warnings[i]);
            }
            RefreshGrid();
            BtnAnalyzeAll_Click(this, EventArgs.Empty);
        }

        #endregion

        #region Helpers

        private void BindStore()
        {
            _store.Changed += () =>
            {
                try
                {
                    if (this.InvokeRequired)
                    {
                        this.Invoke((Action)(() => RefreshGrid()));
                    }
                    else
                    {
                        RefreshGrid();
                    }
                }
                catch { }
            };
        }

        private void RefreshGrid()
        {
            try
            {
                // Manually refresh — BindingList doesn't always trigger DataGridView updates
                gridView.DataSource = null;
                gridView.DataSource = _store.Items;
                gridView.Refresh();
            }
            catch { }
        }

        private WarningTodoItem GetSelectedItem()
        {
            if (gridView.SelectedRows.Count == 0) return null;
            return gridView.SelectedRows[0].DataBoundItem as WarningTodoItem;
        }

        private void ShowDetail(WarningTodoItem item)
        {
            if (item == null)
            {
                txtDetail.Clear();
                txtDiff.Clear();
                return;
            }

            // Detail pane
            txtDetail.Clear();
            txtDetail.SelectionColor = Color.FromArgb(79, 193, 255);
            txtDetail.AppendText($"{item.Code} — {item.StatusText}\n");
            txtDetail.SelectionColor = WARNING_CLR;
            txtDetail.AppendText($"{item.Message}\n\n");

            if (!string.IsNullOrEmpty(item.LlmSummary))
            {
                txtDetail.SelectionColor = SUCCESS;
                txtDetail.AppendText($"💡 {item.LlmSummary}\n");
            }
            if (!string.IsNullOrEmpty(item.LlmExplanation))
            {
                txtDetail.SelectionColor = FG;
                txtDetail.AppendText($"\n{item.LlmExplanation}\n");
            }
            if (!string.IsNullOrEmpty(item.RiskLevel))
            {
                Color riskColor = item.RiskLevel == "High" ? DANGER :
                                  item.RiskLevel == "Medium" ? WARNING_CLR : SUCCESS;
                txtDetail.SelectionColor = riskColor;
                txtDetail.AppendText($"\n⚡ Risk: {item.RiskLevel}\n");
            }
            if (!string.IsNullOrEmpty(item.FailureReason))
            {
                txtDetail.SelectionColor = DANGER;
                txtDetail.AppendText($"\n⚠️ {item.FailureReason}\n");
            }

            // Diff pane
            txtDiff.Clear();
            if (!string.IsNullOrEmpty(item.UnifiedDiff))
            {
                ShowColoredDiff(item.UnifiedDiff);
            }
            else if (!string.IsNullOrEmpty(item.OriginalSnippet) &&
                     !string.IsNullOrEmpty(item.ProposedSnippet))
            {
                txtDiff.SelectionColor = DANGER;
                txtDiff.AppendText("--- Original ---\n");
                txtDiff.SelectionColor = FG;
                txtDiff.AppendText(item.OriginalSnippet + "\n\n");
                txtDiff.SelectionColor = SUCCESS;
                txtDiff.AppendText("+++ Proposed +++\n");
                txtDiff.SelectionColor = FG;
                txtDiff.AppendText(item.ProposedSnippet + "\n");
            }

            // Enable/disable buttons based on status
            btnAccept.Enabled    = item.Status == WarningFixStatus.Suggested;
            btnReject.Enabled    = item.Status == WarningFixStatus.Suggested ||
                                   item.Status == WarningFixStatus.PendingAnalysis;
            btnPreview.Enabled   = item.Status == WarningFixStatus.Suggested;
            btnReanalyze.Enabled = item.Status == WarningFixStatus.Suggested ||
                                   item.Status == WarningFixStatus.Failed     ||
                                   item.Status == WarningFixStatus.Stale      ||
                                   item.Status == WarningFixStatus.Rejected;
        }

        private void ShowColoredDiff(string diff)
        {
            var lines = diff.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("+"))
                {
                    txtDiff.SelectionColor = SUCCESS;
                }
                else if (line.StartsWith("-"))
                {
                    txtDiff.SelectionColor = DANGER;
                }
                else if (line.StartsWith("@@"))
                {
                    txtDiff.SelectionColor = Color.FromArgb(79, 193, 255);
                }
                else
                {
                    txtDiff.SelectionColor = FG;
                }
                txtDiff.AppendText(line + "\n");
            }
        }

        private Button CreateToolButton(string text, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                Height = 26,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = bgColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 4, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Button CreateActionButton(string text, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                Height = 28,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = bgColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                Margin = new Padding(4, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        #endregion
    }
}
