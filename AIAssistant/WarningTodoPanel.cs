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

        // ── Filter controls (created in BuildFilterBar) ──────────────────────
        private ComboBox cmbFilterProject;
        private ComboBox cmbFilterRisk;
        private ComboBox cmbFilterStatus;
        private TextBox  txtFilterCode;
        /// <summary>Prevents RefreshGrid re-entrancy while populating filter dropdowns.</summary>
        private bool _suppressFilterEvents;

        // Track the last item we navigated to so single-click doesn't re-navigate
        // when the grid refreshes and re-selects the same row.
        private Guid _lastNavigatedItemId = Guid.Empty;

        // Refresh debounce: coalesces rapid store-change events into a single grid update.
        // 150ms window prevents UI thrashing during bulk analysis (3 parallel workers).
        private System.Windows.Forms.Timer _refreshDebounce;

        // When true, SelectionChanged suppresses NavigateToItem
        // (prevents file cursor jumping during programmatic grid refresh).
        private bool _suppressNavigation;

        private WarningTodoStore _store;
        private WarningAnalysisQueue _analysisQueue;

        public WarningTodoPanel()
        {
            _store = WarningTodoStore.Instance;
            _analysisQueue = new WarningAnalysisQueue(_store);
            _analysisQueue.ProgressChanged += OnProgressChanged;

            InitializeUI();
            BindStore();

            // Debounce timer: fires 150ms after the last RefreshGrid() call.
            // Prevents expensive DataSource-swap from running on every worker progress event.
            _refreshDebounce = new System.Windows.Forms.Timer { Interval = 150 };
            _refreshDebounce.Tick += (s, e) => { _refreshDebounce.Stop(); DoRefreshGrid(); };
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

            var btnAnalyzeSelected = CreateToolButton("🎯 Analyze Selected", Color.FromArgb(60, 80, 120));
            btnAnalyzeSelected.Click += BtnAnalyzeSelected_Click;

            var btnClear = CreateToolButton("🧹 Clear", BORDER);
            btnClear.Click += BtnClear_Click;

            var btnCancel = CreateToolButton("⏹ Cancel", Color.FromArgb(100, 50, 50));
            btnCancel.Click += BtnCancel_Click;

            // Bulk-retry items that Failed or went Stale
            var btnRetryFailed = CreateToolButton("🔄 Retry Failed", Color.FromArgb(90, 60, 30));
            btnRetryFailed.Click += BtnReanalyzeFailed_Click;

            toolbar.Controls.Add(btnScan);
            toolbar.Controls.Add(btnAnalyzeAll);
            toolbar.Controls.Add(btnAnalyzeSelected);
            toolbar.Controls.Add(btnCancel);
            toolbar.Controls.Add(btnClear);
            toolbar.Controls.Add(btnRetryFailed);

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
            // Initial split: 40% list / 60% detail (F-pattern reading: scan left, read right).
            // User can drag the splitter freely; ratio is only set once on first layout.
            var splitContainer = new SplitContainer
            {
                Dock          = DockStyle.Fill,
                Orientation   = Orientation.Vertical,
                SplitterWidth = 4,           // wider bar for easier grabbing
                BackColor     = BORDER,
                Panel1MinSize = 260,         // list pane must be at least 260px
                Panel2MinSize = 300,         // detail pane must be at least 300px
            };

            bool mainSplitSet = false;
            splitContainer.Resize += (s, e) =>
            {
                if (!mainSplitSet && splitContainer.Width > 100)
                {
                    splitContainer.SplitterDistance = Math.Max(260, (int)(splitContainer.Width * 0.40));
                    mainSplitSet = true;
                }
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
                MultiSelect = true,    // Allow Ctrl/Shift multi-select for Analyze Selected
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

            // Column fill weights (sum = 100 → treated as percentages by Fill mode):
            //   Summary 33%  Project 20%  File 15%  Status 10%  Code 9%  Risk 7%  Line 6%
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status",  HeaderText = "Status",  FillWeight = 10, MinimumWidth = 70,  DataPropertyName = "StatusText" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code",    HeaderText = "Code",    FillWeight =  9, MinimumWidth = 55,  DataPropertyName = "Code" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "File",    HeaderText = "File",    FillWeight = 15, MinimumWidth = 85,  DataPropertyName = "ShortFilePath" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line",    HeaderText = "Line",    FillWeight =  6, MinimumWidth = 38,  DataPropertyName = "Line" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Summary", HeaderText = "Summary", FillWeight = 33, MinimumWidth = 120, DataPropertyName = "LlmSummary" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Risk",    HeaderText = "Risk",    FillWeight =  7, MinimumWidth = 45,  DataPropertyName = "RiskLevel" });
            gridView.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "Project", FillWeight = 20, MinimumWidth = 65,  DataPropertyName = "Project" });

            // Row height: Segoe UI 9pt line height (~12px) + 6px top/bottom padding = 24px
            gridView.RowTemplate.Height = 24;

            // Subtle alternating row tint (avoids wall-of-same-colour effect)
            gridView.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = Color.FromArgb(30, 30, 40),
                ForeColor          = FG,
                SelectionBackColor = Color.FromArgb(38, 79, 120),
                SelectionForeColor = Color.White
            };

            gridView.SelectionChanged += GridView_SelectionChanged;
            gridView.CellDoubleClick  += GridView_CellDoubleClick;
            // NOTE: CellFormatting intentionally not wired (caused rendering errors on Windows).

            splitContainer.Panel1.Controls.Add(gridView);
            splitContainer.Panel1.BackColor = BG;

            // ─── Right: Detail Pane ───
            // Nested horizontal SplitContainer: top 28% = info text, bottom 72% = diff code.
            // Ratio set once on first layout; user can drag the horizontal splitter freely.
            var rightPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = BG,
                Padding   = new Padding(0)
            };

            // Action buttons pinned to the bottom
            var buttonPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 40,
                BackColor     = BG2,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(4, 6, 4, 6),
                WrapContents  = false
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

            // Horizontal split: 28% info text / 72% diff view
            var detailDiffSplit = new SplitContainer
            {
                Dock          = DockStyle.Fill,
                Orientation   = Orientation.Horizontal,
                SplitterWidth = 4,
                BackColor     = BORDER,
                Panel1MinSize = 80,    // min info height
                Panel2MinSize = 100,   // min diff height
            };

            bool detailSplitSet = false;
            detailDiffSplit.Resize += (s, e) =>
            {
                if (!detailSplitSet && detailDiffSplit.Height > 50)
                {
                    detailDiffSplit.SplitterDistance = Math.Max(80, (int)(detailDiffSplit.Height * 0.28));
                    detailSplitSet = true;
                }
            };

            // Top panel — warning info + LLM explanation
            txtDetail = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = BG2,
                ForeColor   = FG,
                Font        = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                Padding     = new Padding(6)
            };
            detailDiffSplit.Panel1.Controls.Add(txtDetail);
            detailDiffSplit.Panel1.BackColor = BG2;

            // Bottom panel — code diff
            var diffLabel = new Label
            {
                Text      = "📝 Proposed Change",
                Dock      = DockStyle.Top,
                Height    = 22,
                ForeColor = Color.FromArgb(79, 193, 255),
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Padding   = new Padding(6, 4, 0, 0),
                BackColor = Color.FromArgb(22, 22, 30)
            };
            txtDiff = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(22, 22, 30),
                ForeColor   = FG,
                Font        = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                WordWrap    = false,
                ScrollBars  = RichTextBoxScrollBars.Both
            };
            detailDiffSplit.Panel2.Controls.Add(txtDiff);
            detailDiffSplit.Panel2.Controls.Add(diffLabel);
            detailDiffSplit.Panel2.BackColor = Color.FromArgb(22, 22, 30);

            rightPanel.Controls.Add(detailDiffSplit);
            rightPanel.Controls.Add(buttonPanel);

            splitContainer.Panel2.Controls.Add(rightPanel);
            splitContainer.Panel2.BackColor = BG;

            // ═══ Assemble ═══
            this.Controls.Add(splitContainer);
            this.Controls.Add(lblStatus);
            this.Controls.Add(BuildFilterBar()); // filter bar sits between toolbar and content
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

        /// <summary>
        /// Analyze only the items currently selected in the DataGridView.
        /// Supports multi-select (Ctrl+Click / Shift+Click) for batch selection.
        /// Items that are not in PendingAnalysis/Failed/Stale status are skipped.
        /// </summary>
        private async void BtnAnalyzeSelected_Click(object sender, EventArgs e)
        {
            if (gridView.SelectedRows.Count == 0)
            {
                lblStatus.Text = "⚠️ No rows selected. Select one or more warnings first.";
                return;
            }

            // Collect selected items that are eligible for (re-)analysis.
            var selected = new System.Collections.Generic.List<WarningTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as WarningTodoItem;
                if (item == null) continue;

                // Allow Pending, Failed, Stale, or even previously Suggested/Rejected items
                // to be explicitly re-analyzed on user request.
                if (item.Status == WarningFixStatus.Analyzing) continue; // already in flight
                if (item.Status == WarningFixStatus.Accepted)  continue; // already applied

                selected.Add(item);
            }

            if (selected.Count == 0)
            {
                lblStatus.Text = "ℹ️ Selected items are already analyzed or accepted.";
                return;
            }

            lblStatus.Text = $"🎯 Analyzing {selected.Count} selected warning(s)...";

            // Reset LLM fields for items that had prior results (re-analyze case)
            _analysisQueue.ResetAndEnqueue(selected);
            RefreshGrid();

            await _analysisQueue.ProcessAllAsync();

            try
            {
                this.Invoke((Action)(() =>
                {
                    RefreshGrid();
                    lblStatus.Text = $"Selected analysis complete. {_store.GetStatusSummary()}";
                }));
            }
            catch { }
        }

        /// <summary>
        /// Cancel all in-flight LLM analysis, drain the queue, and
        /// revert any items that were stuck in Analyzing back to PendingAnalysis.
        /// </summary>
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            _analysisQueue.Cancel();

            // Revert items that got marked Analyzing but never completed.
            int reverted = 0;
            foreach (var item in _store.Items)
            {
                if (item.Status == WarningFixStatus.Analyzing)
                {
                    item.Status = WarningFixStatus.PendingAnalysis;
                    item.FailureReason = null;
                    item.UpdatedAt = DateTimeOffset.Now;
                    reverted++;
                }
            }

            if (reverted > 0)
                _store.NotifyChanged();

            RefreshGrid();
            lblStatus.Text = reverted > 0
                ? $"⏹ Cancelled. {reverted} item(s) reverted to Pending."
                : "⏹ Cancelled.";
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _store.Clear();
            RefreshGrid();
            txtDetail.Clear();
            txtDiff.Clear();
            lblStatus.Text = "Cleared.";
        }

        private async void BtnReanalyzeFailed_Click(object sender, EventArgs e)
        {
            var failedItems = _store.Items
                .Where(x => x.Status == WarningFixStatus.Failed ||
                            x.Status == WarningFixStatus.Stale)
                .ToList();

            if (failedItems.Count == 0)
            {
                lblStatus.Text = "ℹ️ No failed or stale items to re-analyze.";
                return;
            }

            lblStatus.Text = $"🔄 Queuing {failedItems.Count} failed/stale item(s) for re-analysis...";

            _analysisQueue.ResetAndEnqueue(failedItems);
            RefreshGrid();

            await _analysisQueue.ProcessAllAsync();

            try
            {
                this.Invoke((Action)(() =>
                {
                    RefreshGrid();
                    lblStatus.Text = $"Retry complete. {_store.GetStatusSummary()}";
                }));
            }
            catch { }
        }

        private void BtnAccept_Click(object sender, EventArgs e)
        {
            // Collect all selected items that are in Suggested state.
            var targets = new System.Collections.Generic.List<WarningTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as WarningTodoItem;
                if (item != null && item.Status == WarningFixStatus.Suggested)
                    targets.Add(item);
            }

            if (targets.Count == 0)
            {
                lblStatus.Text = "ℹ️ No suggested fixes selected to apply.";
                return;
            }

            // When applying multiple fixes in the SAME file, earlier fixes shift line
            // offsets for later ones.  Sort by file path then by LINE DESCENDING so
            // bottom-of-file fixes are applied first (their offsets won't be affected
            // by changes above them).
            targets.Sort((a, b) =>
            {
                int cmp = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return b.Line.CompareTo(a.Line); // descending line order within same file
            });

            int ok = 0, fail = 0;
            string lastError = null;
            WarningTodoItem lastApplied = null;

            foreach (var item in targets)
            {
                string error = WarningFixApplier.Apply(item);
                if (error == null)
                {
                    ok++;
                    lastApplied = item;
                }
                else
                {
                    fail++;
                    lastError = error;
                }
            }

            _store.NotifyChanged();

            // Navigate to the last successfully applied item.
            if (lastApplied != null)
            {
                _lastNavigatedItemId = Guid.Empty;
                NavigateToItem(lastApplied);
            }

            // Status summary
            if (fail == 0)
                lblStatus.Text = ok == 1
                    ? $"✅ Fix applied to {targets[0].ShortFilePath}. Backup saved."
                    : $"✅ {ok} fixes applied successfully. Backups saved.";
            else
                lblStatus.Text = ok == 0
                    ? $"⚠️ All {fail} fix(es) failed. Last error: {lastError}"
                    : $"⚠️ {ok} applied, {fail} failed. Last error: {lastError}";

            RefreshGrid();

            // Show detail for the first selected item (or last applied).
            var detailItem = lastApplied ?? targets[0];
            ShowDetail(detailItem);
        }

        private void BtnReject_Click(object sender, EventArgs e)
        {
            int count = 0;
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as WarningTodoItem;
                if (item == null) continue;
                if (item.Status == WarningFixStatus.Accepted) continue; // don't un-accept

                _store.Reject(item);
                count++;
            }

            RefreshGrid();
            lblStatus.Text = count == 1
                ? $"❌ Rejected fix for selected item."
                : $"❌ Rejected {count} item(s).";
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
            // _suppressNavigation is set to true while DoRefreshGrid runs.
            if (!_suppressNavigation && item.Id != _lastNavigatedItemId)
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

        /// <summary>
        /// Debounced entry point for grid refresh. Coalesces rapid calls (e.g., from
        /// parallel worker progress events) into a single actual update 150ms after
        /// the last call. This prevents UI thrashing during bulk analysis.
        /// </summary>
        private void RefreshGrid()
        {
            // Restart the timer — if called again within 150ms the clock resets.
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }

        /// <summary>
        /// Performs the actual DataGridView rebind. Preserves the selected item
        /// and suppresses VS navigation so the cursor doesn't jump on every update.
        /// </summary>
        private void DoRefreshGrid()
        {
            try
            {
                // ① Remember what was selected BEFORE the DataSource swap.
                //    DataSource = null clears selection, so we must capture the Id first.
                Guid prevId = GetSelectedItem()?.Id ?? Guid.Empty;

                // ② Suppress navigation for the entire rebind sequence.
                //    Without this flag, each SelectionChanged event during the swap
                //    would call NavigateToItem and jump the VS cursor.
                _suppressNavigation = true;
                try
                {
                    RefreshProjectFilter();

                    var filtered = GetFilteredItems().ToList();
                    gridView.DataSource = null;
                    gridView.DataSource = filtered;
                    gridView.Refresh();

                    // ③ Restore the previous selection (same object, different list position).
                    if (prevId != Guid.Empty)
                    {
                        for (int i = 0; i < gridView.Rows.Count; i++)
                        {
                            if ((gridView.Rows[i].DataBoundItem as WarningTodoItem)?.Id == prevId)
                            {
                                gridView.ClearSelection();
                                gridView.Rows[i].Selected = true;

                                // Keep the row visible if it's scrolled off-screen.
                                int firstVisible = gridView.FirstDisplayedScrollingRowIndex;
                                int visibleCount  = gridView.DisplayedRowCount(false);
                                if (i < firstVisible || i >= firstVisible + visibleCount)
                                    gridView.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 2);

                                break;
                            }
                        }
                    }
                }
                finally
                {
                    // ④ Restore navigation AFTER selection is settled.
                    _suppressNavigation = false;
                }
            }
            catch { }
        }

        private WarningTodoItem GetSelectedItem()
        {
            if (gridView.SelectedRows.Count == 0) return null;
            return gridView.SelectedRows[0].DataBoundItem as WarningTodoItem;
        }

        // ─── Filter helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Apply current filter bar selections to _store.Items.
        /// Returns the matching subset (same object references — safe for GetSelectedItem).
        /// </summary>
        private IEnumerable<WarningTodoItem> GetFilteredItems()
        {
            IEnumerable<WarningTodoItem> items = _store.Items;

            // Project
            string project = cmbFilterProject?.SelectedItem as string;
            if (project != null && project != "(All)")
                items = items.Where(x =>
                    string.Equals(x.Project, project, StringComparison.OrdinalIgnoreCase));

            // Risk level
            string risk = cmbFilterRisk?.SelectedItem as string;
            if (risk != null && risk != "(All)")
                items = items.Where(x =>
                    string.Equals(x.RiskLevel, risk, StringComparison.OrdinalIgnoreCase));

            // Status — ComboBox items match WarningTodoItem.StatusText exactly
            string statusDisplay = cmbFilterStatus?.SelectedItem as string;
            if (statusDisplay != null && statusDisplay != "(All)")
                items = items.Where(x => x.StatusText == statusDisplay);

            // Code / message full-text search
            string query = txtFilterCode?.Text?.Trim();
            if (!string.IsNullOrEmpty(query))
                items = items.Where(x =>
                    (x.Code    ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Message ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            return items;
        }

        /// <summary>
        /// Rebuild the Project dropdown from current store data, preserving the
        /// user's current selection. Suppresses filter events to avoid re-entrancy.
        /// </summary>
        private void RefreshProjectFilter()
        {
            if (cmbFilterProject == null) return;
            _suppressFilterEvents = true;
            try
            {
                string prev     = cmbFilterProject.SelectedItem as string;
                var projects    = new System.Collections.Generic.List<string> { "(All)" };
                projects.AddRange(
                    _store.Items
                          .Select(x => x.Project ?? "")
                          .Where(p => p.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(p => p));

                // Avoid visual flicker if the list hasn't changed
                if (cmbFilterProject.Items.Count != projects.Count ||
                    !projects.SequenceEqual(cmbFilterProject.Items.Cast<string>()))
                {
                    cmbFilterProject.DataSource = null;
                    cmbFilterProject.DataSource = projects;
                }

                int idx = projects.IndexOf(prev ?? "(All)");
                cmbFilterProject.SelectedIndex = Math.Max(0, idx);
            }
            finally
            {
                _suppressFilterEvents = false;
            }
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_suppressFilterEvents) return;
            RefreshGrid();
        }

        /// <summary>
        /// Build the filter bar panel that sits between the toolbar and the split view.
        /// </summary>
        private Panel BuildFilterBar()
        {
            var bar = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 30,
                BackColor = Color.FromArgb(32, 32, 42),
                Padding   = new Padding(6, 3, 6, 3)
            };

            var layout = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                AutoSize      = false
            };

            Func<string, Label> mkLabel = text => new Label
            {
                Text      = text,
                AutoSize  = true,
                ForeColor = Color.FromArgb(140, 143, 150),
                Padding   = new Padding(0, 4, 2, 0),
                Font      = new Font("Segoe UI", 8.5f)
            };

            Func<int, ComboBox> mkCombo = width =>
            {
                var cmb = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width         = width,
                    FlatStyle     = FlatStyle.Flat,
                    BackColor     = Color.FromArgb(50, 50, 60),
                    ForeColor     = Color.White,
                    Font          = new Font("Segoe UI", 8.5f),
                    Margin        = new Padding(0, 2, 8, 0)
                };

                // Auto-expand dropdown width to fit the longest item text.
                // Measured on each open so dynamically-added items (e.g. Project list) are covered.
                cmb.DropDown += (s, ev) =>
                {
                    var cb = (ComboBox)s;
                    int maxWidth = cb.Width;
                    using (var g = cb.CreateGraphics())
                    {
                        foreach (var item in cb.Items)
                        {
                            int w = (int)g.MeasureString(item.ToString(), cb.Font).Width
                                    + SystemInformation.VerticalScrollBarWidth + 4;
                            if (w > maxWidth) maxWidth = w;
                        }
                    }
                    cb.DropDownWidth = maxWidth;
                };

                return cmb;
            };

            // Code / Message search
            layout.Controls.Add(mkLabel("🔍"));
            txtFilterCode = new TextBox
            {
                Width       = 130,
                BackColor   = Color.FromArgb(50, 50, 60),
                ForeColor   = Color.White,
                Font        = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.FixedSingle,
                Margin      = new Padding(0, 2, 10, 0)
            };
            txtFilterCode.TextChanged += OnFilterChanged;
            layout.Controls.Add(txtFilterCode);

            // Project
            layout.Controls.Add(mkLabel("Project:"));
            cmbFilterProject = mkCombo(130);
            cmbFilterProject.Items.Add("(All)");
            cmbFilterProject.SelectedIndex = 0;
            cmbFilterProject.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterProject);

            // Risk
            layout.Controls.Add(mkLabel("Risk:"));
            cmbFilterRisk = mkCombo(82);
            cmbFilterRisk.Items.AddRange(new object[] { "(All)", "Low", "Medium", "High" });
            cmbFilterRisk.SelectedIndex = 0;
            cmbFilterRisk.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterRisk);

            // Status — strings match WarningTodoItem.StatusText exactly
            layout.Controls.Add(mkLabel("Status:"));
            cmbFilterStatus = mkCombo(115);
            cmbFilterStatus.Items.AddRange(new object[]
            {
                "(All)",
                "⏳ Pending", "🔄 Analyzing", "💡 Suggested",
                "✅ Accepted", "❌ Rejected", "⚠️ Failed", "🔒 Stale"
            });
            cmbFilterStatus.SelectedIndex = 0;
            cmbFilterStatus.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterStatus);

            // Clear filters
            var btnClearFilters = new Button
            {
                Text      = "✕",
                Width     = 26,
                Height    = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 50, 50),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8f),
                Margin    = new Padding(4, 2, 0, 0),
                Cursor    = Cursors.Hand
            };
            btnClearFilters.FlatAppearance.BorderSize = 0;
            btnClearFilters.Click += (s, e) =>
            {
                _suppressFilterEvents = true;
                try
                {
                    txtFilterCode.Clear();
                    cmbFilterProject.SelectedIndex = 0;
                    cmbFilterRisk.SelectedIndex    = 0;
                    cmbFilterStatus.SelectedIndex  = 0;
                }
                finally
                {
                    _suppressFilterEvents = false;
                }
                RefreshGrid();
            };
            layout.Controls.Add(btnClearFilters);

            bar.Controls.Add(layout);
            return bar;
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
