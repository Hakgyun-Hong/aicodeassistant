using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// SAM Fix Assistant UI Panel — docked Visual Studio tool window.
    /// Loads a Samsung SAM quality report (local file or URL), shows violations,
    /// and uses LLM to suggest and apply code fixes.
    /// Design mirrors WarningTodoPanel for consistency.
    /// </summary>
    public sealed class SamTodoPanel : UserControl
    {
        // ─── Color palette (same as WarningTodoPanel) ─────────────────────
        private static readonly Color BG     = Color.FromArgb(28, 28, 38);
        private static readonly Color BG2    = Color.FromArgb(35, 35, 48);
        private static readonly Color FG     = Color.FromArgb(220, 220, 230);
        private static readonly Color ACCENT = Color.FromArgb(86, 156, 214);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 80);

        // ─── Services ─────────────────────────────────────────────────────
        private readonly SamTodoStore     _store;
        private readonly SamAnalysisQueue _analysisQueue;

        // ─── UI controls ──────────────────────────────────────────────────
        private DataGridView gridView;
        private RichTextBox  txtDetail;
        private RichTextBox  txtDiff;
        private Label        lblStatus;
        private Button       btnReanalyze;
        private TextBox      txtSourceRoot;

        // ─── Filter controls ──────────────────────────────────────────────
        private ComboBox cmbFilterMetric;
        private ComboBox cmbFilterRisk;
        private ComboBox cmbFilterStatus;
        private TextBox  txtFilterSearch;
        private bool     _suppressFilterEvents;

        // ─── State ────────────────────────────────────────────────────────
        private Guid   _lastNavigatedItemId = Guid.Empty;
        private bool   _suppressNavigation  = false;
        private Timer  _debounceTimer;

        // ─── Constructor ──────────────────────────────────────────────────

        public SamTodoPanel()
        {
            _store         = SamTodoStore.Instance;
            _analysisQueue = new SamAnalysisQueue(_store, maxParallel: 3);

            _debounceTimer = new Timer { Interval = 150 };
            _debounceTimer.Tick += (s, e) => { _debounceTimer.Stop(); DoRefreshGrid(); };

            _store.Changed += () =>
            {
                if (InvokeRequired) BeginInvoke((Action)RefreshGrid);
                else RefreshGrid();
            };

            _analysisQueue.ProgressChanged += (done, total) =>
            {
                if (InvokeRequired)
                    BeginInvoke((Action)(() => lblStatus.Text = $"Analyzing... {done}/{total}"));
                else
                    lblStatus.Text = $"Analyzing... {done}/{total}";
            };

            BuildUI();
        }

        // ─── UI Construction ──────────────────────────────────────────────

        private void BuildUI()
        {
            BackColor = BG;
            Padding   = new Padding(0);

            // ── Toolbar ──
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                BackColor = BG2, Padding = new Padding(6, 5, 6, 5),
                WrapContents = false, FlowDirection = FlowDirection.LeftToRight
            };

            var btnLoadFile = CreateToolButton("📂 Load File", ACCENT);
            btnLoadFile.Click += BtnLoadFile_Click;

            var btnLoadUrl = CreateToolButton("🌐 Load URL", Color.FromArgb(60, 100, 60));
            btnLoadUrl.Click += BtnLoadUrl_Click;

            var btnAnalyzeAll = CreateToolButton("⚡ Analyze All", Color.FromArgb(79, 60, 120));
            btnAnalyzeAll.Click += BtnAnalyzeAll_Click;

            var btnAnalyzeSelected = CreateToolButton("🎯 Analyze Selected", Color.FromArgb(60, 80, 120));
            btnAnalyzeSelected.Click += BtnAnalyzeSelected_Click;

            var btnCancel = CreateToolButton("⏹ Cancel", Color.FromArgb(100, 50, 50));
            btnCancel.Click += BtnCancel_Click;

            var btnClear = CreateToolButton("🧹 Clear", BORDER);
            btnClear.Click += (s, e) => { _store.Clear(); RefreshGrid(); lblStatus.Text = "Cleared."; };

            var btnRetryFailed = CreateToolButton("🔄 Retry Failed", Color.FromArgb(90, 60, 30));
            btnRetryFailed.Click += BtnRetryFailed_Click;

            toolbar.Controls.AddRange(new Control[]
                { btnLoadFile, btnLoadUrl, btnAnalyzeAll, btnAnalyzeSelected, btnCancel, btnClear, btnRetryFailed });

            // ── Source Root row ──
            var rootBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 28,
                BackColor = BG2, Padding = new Padding(6, 3, 6, 3),
                WrapContents = false
            };
            var lblRoot = new Label { Text = "Source Root:", AutoSize = true, ForeColor = FG,
                                      Padding = new Padding(0, 3, 4, 0), Font = new Font("Segoe UI", 8.5f) };
            txtSourceRoot = new TextBox
            {
                Width = 400, BackColor = Color.FromArgb(50, 50, 60), ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f), BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 6, 0)
            };
            // Default: try VS solution directory
            TrySetDefaultSourceRoot();

            var btnBrowseRoot = new Button
            {
                Text = "Browse…", Height = 22, AutoSize = true,
                BackColor = Color.FromArgb(50, 50, 60), ForeColor = FG,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f),
                Margin = new Padding(0, 2, 0, 0)
            };
            btnBrowseRoot.FlatAppearance.BorderColor = BORDER;
            btnBrowseRoot.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Select source root directory";
                    if (dlg.ShowDialog() == DialogResult.OK)
                        txtSourceRoot.Text = dlg.SelectedPath;
                }
            };
            rootBar.Controls.AddRange(new Control[] { lblRoot, txtSourceRoot, btnBrowseRoot });

            // ── Filter Bar ──
            var filterBar = BuildFilterBar();

            // ── Status bar ──
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom, Height = 22, Text = "Load a SAM report to begin.",
                BackColor = Color.FromArgb(20, 20, 30), ForeColor = Color.FromArgb(150, 150, 180),
                Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            // ── Main split: grid (40%) / detail (60%) ──
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                BackColor = BORDER, Panel1MinSize = 200, Panel2MinSize = 200
            };
            bool mainSplitSet = false;
            mainSplit.Layout += (s, e) =>
            {
                if (!mainSplitSet && mainSplit.Width > 100)
                {
                    mainSplit.SplitterDistance = Math.Max(200, (int)(mainSplit.Width * 0.40));
                    mainSplitSet = true;
                }
            };

            // Left: grid
            gridView = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = BG, ForeColor = FG, GridColor = BORDER,
                BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Font = new Font("Segoe UI", 9f),
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BG, ForeColor = FG,
                    SelectionBackColor = Color.FromArgb(38, 79, 120), SelectionForeColor = Color.White
                },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(33, 33, 45)
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BG2, ForeColor = Color.FromArgb(180, 180, 200), Font = new Font("Segoe UI", 8.5f)
                },
                RowTemplate = { Height = 24 }
            };
            gridView.SelectionChanged += GridView_SelectionChanged;
            SetupGridColumns();
            mainSplit.Panel1.Controls.Add(gridView);

            // Right: detail split (28% info / 72% diff)
            var detailSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                BackColor = BORDER, Panel1MinSize = 60, Panel2MinSize = 80
            };
            bool detailSplitSet = false;
            detailSplit.Layout += (s, e) =>
            {
                if (!detailSplitSet && detailSplit.Height > 100)
                {
                    detailSplit.SplitterDistance = Math.Max(60, (int)(detailSplit.Height * 0.28));
                    detailSplitSet = true;
                }
            };

            txtDetail = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = BG2, ForeColor = FG,
                Font = new Font("Segoe UI", 9f), ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            txtDiff = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 30), ForeColor = FG,
                Font = new Font("Cascadia Code", 9f, FontStyle.Regular, GraphicsUnit.Point,
                    0, false) is { } f ? f : new Font("Courier New", 9f),
                ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Both, WordWrap = false
            };
            detailSplit.Panel1.Controls.Add(txtDetail);
            detailSplit.Panel2.Controls.Add(txtDiff);
            mainSplit.Panel2.Controls.Add(detailSplit);

            // ── Action buttons ──
            var actionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 32, BackColor = BG2,
                Padding = new Padding(4, 4, 4, 4), WrapContents = false
            };
            var btnAccept  = CreateActionButton("✅ Accept & Apply", Color.FromArgb(40, 90, 40));
            var btnReject  = CreateActionButton("❌ Reject",          Color.FromArgb(90, 40, 40));
            btnReanalyze   = CreateActionButton("🔄 Re-analyze",      Color.FromArgb(50, 60, 90));
            var btnPreview = CreateActionButton("🔎 Preview",         Color.FromArgb(50, 50, 70));

            btnAccept.Click  += BtnAccept_Click;
            btnReject.Click  += BtnReject_Click;
            btnReanalyze.Click += BtnReanalyze_Click;
            btnPreview.Click += BtnPreview_Click;

            actionBar.Controls.AddRange(new Control[] { btnAccept, btnReject, btnReanalyze, btnPreview });

            // ── Assemble ──
            Controls.Add(mainSplit);
            Controls.Add(actionBar);
            Controls.Add(filterBar);
            Controls.Add(rootBar);
            Controls.Add(toolbar);
            Controls.Add(lblStatus);
        }

        private void SetupGridColumns()
        {
            gridView.Columns.Clear();
            Add("StatusText",    "Status",   60,  8);
            Add("Metric",        "Metric",   50,  5);
            Add("LlmSummary",    "Summary",  33,  5);
            Add("ShortFilePath", "File",     18,  5);
            Add("LineDisplay",   "Line",     7,   5);
            Add("Value",         "Value",    6,   5);
            Add("RiskLevel",     "Risk",     7,   5);
            Add("Entity",        "Entity",   14,  5);

            void Add(string prop, string header, int fillWeight, int minWidth)
            {
                gridView.Columns.Add(new DataGridViewTextBoxColumn
                {
                    DataPropertyName = prop, HeaderText = header,
                    FillWeight = fillWeight, MinimumWidth = minWidth
                });
            }
        }

        // ─── Filter Bar ────────────────────────────────────────────────────

        private Panel BuildFilterBar()
        {
            var bar = new Panel
            {
                Dock = DockStyle.Top, Height = 30,
                BackColor = Color.FromArgb(32, 32, 42), Padding = new Padding(6, 3, 6, 3)
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, AutoSize = false
            };

            Label mkLabel(string t) => new Label
            {
                Text = t, AutoSize = true, ForeColor = Color.FromArgb(140, 143, 150),
                Padding = new Padding(0, 4, 2, 0), Font = new Font("Segoe UI", 8.5f)
            };

            ComboBox mkCombo(int w)
            {
                var c = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, Width = w,
                    FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f),
                    Margin = new Padding(0, 2, 8, 0)
                };
                c.DropDown += (s, e) =>
                {
                    var cb = (ComboBox)s;
                    int max = cb.Width;
                    using (var g = cb.CreateGraphics())
                        foreach (var item in cb.Items)
                        {
                            int w2 = (int)g.MeasureString(item.ToString(), cb.Font).Width
                                     + SystemInformation.VerticalScrollBarWidth + 4;
                            if (w2 > max) max = w2;
                        }
                    cb.DropDownWidth = max;
                };
                return c;
            }

            layout.Controls.Add(mkLabel("🔍"));
            txtFilterSearch = new TextBox
            {
                Width = 130, BackColor = Color.FromArgb(50, 50, 60), ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f), BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 10, 0)
            };
            txtFilterSearch.TextChanged += OnFilterChanged;
            layout.Controls.Add(txtFilterSearch);

            layout.Controls.Add(mkLabel("Metric:"));
            cmbFilterMetric = mkCombo(100);
            cmbFilterMetric.Items.Add("(All)");
            cmbFilterMetric.Items.AddRange(new object[] { "CC","DC","MCD","CBO","LOC","GV","PP","NCS","DepC","GM" });
            cmbFilterMetric.SelectedIndex = 0;
            cmbFilterMetric.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterMetric);

            layout.Controls.Add(mkLabel("Risk:"));
            cmbFilterRisk = mkCombo(82);
            cmbFilterRisk.Items.AddRange(new object[] { "(All)", "Low", "Medium", "High" });
            cmbFilterRisk.SelectedIndex = 0;
            cmbFilterRisk.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterRisk);

            layout.Controls.Add(mkLabel("Status:"));
            cmbFilterStatus = mkCombo(120);
            cmbFilterStatus.Items.AddRange(new object[]
                { "(All)", "Pending", "Analyzing", "Suggested", "Accepted", "Rejected", "Failed", "Arch.Only" });
            cmbFilterStatus.SelectedIndex = 0;
            cmbFilterStatus.SelectedIndexChanged += OnFilterChanged;
            layout.Controls.Add(cmbFilterStatus);

            var btnClearF = new Button
            {
                Text = "✕", Width = 24, Height = 22, BackColor = Color.FromArgb(50, 50, 60),
                ForeColor = FG, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f),
                Margin = new Padding(0, 2, 0, 0)
            };
            btnClearF.FlatAppearance.BorderSize = 0;
            btnClearF.Click += (s, e) =>
            {
                _suppressFilterEvents = true;
                txtFilterSearch.Text = "";
                cmbFilterMetric.SelectedIndex = 0;
                cmbFilterRisk.SelectedIndex   = 0;
                cmbFilterStatus.SelectedIndex = 0;
                _suppressFilterEvents = false;
                RefreshGrid();
            };
            layout.Controls.Add(btnClearF);

            bar.Controls.Add(layout);
            return bar;
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_suppressFilterEvents) return;
            RefreshGrid();
        }

        // ─── Grid refresh ──────────────────────────────────────────────────

        private void RefreshGrid()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DoRefreshGrid()
        {
            if (InvokeRequired) { Invoke((Action)DoRefreshGrid); return; }

            _suppressNavigation = true;
            Guid prevId = GetSelectedItem()?.Id ?? Guid.Empty;

            var filtered = GetFilteredItems().ToList();
            gridView.DataSource = null;
            gridView.DataSource = filtered;

            // Restore selection
            if (prevId != Guid.Empty)
            {
                for (int r = 0; r < gridView.Rows.Count; r++)
                {
                    if ((gridView.Rows[r].DataBoundItem as SamTodoItem)?.Id == prevId)
                    {
                        gridView.Rows[r].Selected = true;
                        break;
                    }
                }
            }
            _suppressNavigation = false;
        }

        private IEnumerable<SamTodoItem> GetFilteredItems()
        {
            var items = (IEnumerable<SamTodoItem>)_store.Items;

            string metric = cmbFilterMetric?.SelectedItem as string;
            if (!string.IsNullOrEmpty(metric) && metric != "(All)")
                items = items.Where(x => x.Metric == metric);

            string risk = cmbFilterRisk?.SelectedItem as string;
            if (!string.IsNullOrEmpty(risk) && risk != "(All)")
                items = items.Where(x => string.Equals(x.RiskLevel, risk, StringComparison.OrdinalIgnoreCase));

            string statusDisplay = cmbFilterStatus?.SelectedItem as string;
            if (!string.IsNullOrEmpty(statusDisplay) && statusDisplay != "(All)")
                items = items.Where(x => x.StatusText.Contains(statusDisplay));

            string query = txtFilterSearch?.Text?.Trim();
            if (!string.IsNullOrEmpty(query))
                items = items.Where(x =>
                    (x.RelativePath ?? "").Contains(query) ||
                    (x.Entity ?? "").Contains(query) ||
                    (x.LlmSummary ?? "").Contains(query) ||
                    (x.Metric ?? "").Contains(query));

            return items;
        }

        // ─── Toolbar handlers ──────────────────────────────────────────────

        private void BtnLoadFile_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title  = "Open SAM Quality Report",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                LoadAndParse(() => SamReportParser.ParseFile(dlg.FileName), dlg.FileName);
            }
        }

        private void BtnLoadUrl_Click(object sender, EventArgs e)
        {
            string url = PromptForInput("Load SAM Report from URL",
                "Enter the SAM report URL:", "https://");
            if (string.IsNullOrWhiteSpace(url)) return;

            lblStatus.Text = $"Loading {url}...";
            LoadAndParse(() => SamReportParser.ParseUrl(url), url);
        }

        private void LoadAndParse(Func<List<SamTodoItem>> loader, string source)
        {
            try
            {
                lblStatus.Text = $"Parsing {Path.GetFileName(source)}...";
                var items = loader();

                string root = txtSourceRoot.Text.Trim();
                int resolved = 0;
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(item.RelativePath))
                    {
                        string abs = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(abs))
                        {
                            item.FilePath = abs;
                            resolved++;
                        }
                    }
                    _store.AddOrUpdate(item);
                }

                RefreshGrid();
                lblStatus.Text = $"Loaded {items.Count} items ({resolved} paths resolved). {_store.GetStatusSummary()}";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ Load failed: {ex.Message}";
                MessageBox.Show($"Failed to load SAM report:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnAnalyzeAll_Click(object sender, EventArgs e)
        {
            var pending = _store.Items
                .Where(x => x.Status == SamFixStatus.PendingAnalysis)
                .ToList();

            if (pending.Count == 0) { lblStatus.Text = "No pending items to analyze."; return; }
            lblStatus.Text = $"Analyzing {pending.Count} items...";

            foreach (var item in pending)
                _analysisQueue.Enqueue(item);

            await _analysisQueue.ProcessAllAsync();

            Invoke((Action)(() =>
            {
                RefreshGrid();
                lblStatus.Text = _store.GetStatusSummary();
            }));
        }

        private async void BtnAnalyzeSelected_Click(object sender, EventArgs e)
        {
            if (gridView.SelectedRows.Count == 0)
            { lblStatus.Text = "⚠️ No rows selected."; return; }

            var selected = new List<SamTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as SamTodoItem;
                if (item == null) continue;
                if (item.Status == SamFixStatus.Analyzing) continue;
                if (item.Status == SamFixStatus.Accepted)  continue;
                selected.Add(item);
            }

            if (selected.Count == 0) { lblStatus.Text = "ℹ️ Selected items already analyzed or accepted."; return; }
            lblStatus.Text = $"🎯 Analyzing {selected.Count} selected item(s)...";

            _analysisQueue.ResetAndEnqueue(selected);
            RefreshGrid();
            await _analysisQueue.ProcessAllAsync();

            try
            {
                Invoke((Action)(() =>
                {
                    RefreshGrid();
                    lblStatus.Text = $"Selected analysis complete. {_store.GetStatusSummary()}";
                }));
            }
            catch { }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            _analysisQueue.Cancel();
            int reverted = 0;
            foreach (var item in _store.Items)
            {
                if (item.Status == SamFixStatus.Analyzing)
                {
                    item.Status = SamFixStatus.PendingAnalysis;
                    item.FailureReason = null;
                    reverted++;
                }
            }
            if (reverted > 0) _store.NotifyChanged();
            RefreshGrid();
            lblStatus.Text = reverted > 0 ? $"⏹ Cancelled. {reverted} item(s) reverted to Pending." : "⏹ Cancelled.";
        }

        private async void BtnRetryFailed_Click(object sender, EventArgs e)
        {
            var failed = _store.Items
                .Where(x => x.Status == SamFixStatus.Failed || x.Status == SamFixStatus.Stale)
                .ToList();
            if (failed.Count == 0) { lblStatus.Text = "ℹ️ No failed/stale items."; return; }

            lblStatus.Text = $"🔄 Retrying {failed.Count} item(s)...";
            _analysisQueue.ResetAndEnqueue(failed);
            RefreshGrid();
            await _analysisQueue.ProcessAllAsync();

            try { Invoke((Action)(() => { RefreshGrid(); lblStatus.Text = _store.GetStatusSummary(); })); }
            catch { }
        }

        // ─── Action button handlers ────────────────────────────────────────

        private void BtnAccept_Click(object sender, EventArgs e)
        {
            var targets = new List<SamTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as SamTodoItem;
                if (item != null && item.Status == SamFixStatus.Suggested && item.HasCodeLocation)
                    targets.Add(item);
            }
            if (targets.Count == 0) { lblStatus.Text = "ℹ️ No applicable suggested items selected."; return; }

            // Apply bottom-up within same file to preserve offsets
            targets.Sort((a, b) =>
            {
                int c = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : b.LineNo.CompareTo(a.LineNo);
            });

            int ok = 0, fail = 0;
            string lastError = null;
            SamTodoItem lastApplied = null;

            foreach (var item in targets)
            {
                // Delegate to WarningFixApplier — reuse the 3-Tier snippet matching.
                // We create a temporary WarningTodoItem bridge.
                var bridge = new WarningTodoItem
                {
                    FilePath        = item.FilePath,
                    Line            = item.LineNo,
                    OriginalSnippet = item.OriginalSnippet,
                    ProposedSnippet = item.ProposedSnippet,
                    OriginalFileHash= item.OriginalFileHash,
                    Code            = item.Metric,
                    Status          = WarningFixStatus.Suggested
                };
                string error = WarningFixApplier.Apply(bridge);
                if (error == null)
                {
                    item.Status    = SamFixStatus.Accepted;
                    item.UpdatedAt = DateTimeOffset.Now;
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

            if (lastApplied != null)
            {
                _lastNavigatedItemId = Guid.Empty;
                NavigateToItem(lastApplied);
            }

            if (fail == 0)
                lblStatus.Text = ok == 1 ? $"✅ Fix applied." : $"✅ {ok} fixes applied.";
            else
                lblStatus.Text = ok == 0 ? $"⚠️ All {fail} failed: {lastError}" : $"⚠️ {ok} applied, {fail} failed: {lastError}";

            RefreshGrid();
            if (lastApplied != null) ShowDetail(lastApplied);
        }

        private void BtnReject_Click(object sender, EventArgs e)
        {
            int count = 0;
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as SamTodoItem;
                if (item == null || item.Status == SamFixStatus.Accepted) continue;
                _store.Reject(item);
                count++;
            }
            RefreshGrid();
            lblStatus.Text = count == 1 ? "❌ Rejected." : $"❌ Rejected {count} item(s).";
        }

        private async void BtnReanalyze_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;
            btnReanalyze.Enabled = false;
            lblStatus.Text = $"🔄 Re-analyzing {item.Metric} in {item.ShortFilePath}...";
            await _analysisQueue.ReanalyzeAsync(item);
            try
            {
                Invoke((Action)(() =>
                {
                    RefreshGrid();
                    ShowDetail(item);
                    btnReanalyze.Enabled = true;
                    lblStatus.Text = $"Re-analysis complete: {item.StatusText}";
                }));
            }
            catch { }
        }

        private void BtnPreview_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null || (string.IsNullOrWhiteSpace(item.OriginalSnippet) &&
                                 string.IsNullOrWhiteSpace(item.LlmExplanation))) return;

            var dlg = new CodeReviewDialog(
                $"SAM Fix: {item.Metric} ({item.MetricFullName})",
                item.OriginalSnippet ?? "(no original snippet)",
                $"```\n{item.ProposedSnippet}\n```\n\n{item.LlmExplanation}");
            dlg.ShowDialog();
        }

        // ─── Grid / Detail helpers ─────────────────────────────────────────

        private void GridView_SelectionChanged(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;
            ShowDetail(item);

            if (_suppressNavigation || item.Id == _lastNavigatedItemId) return;
            if (item.HasCodeLocation && !string.IsNullOrEmpty(item.FilePath))
                NavigateToItem(item);
        }

        private void ShowDetail(SamTodoItem item)
        {
            if (item == null) { txtDetail.Clear(); txtDiff.Clear(); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{item.Metric}] {item.MetricFullName}  —  {item.StatusText}");
            sb.AppendLine($"File: {item.RelativePath}  Line: {item.LineDisplay}");
            if (!string.IsNullOrEmpty(item.Entity))    sb.AppendLine($"Entity: {item.Entity}");
            if (item.Value > 0)                         sb.AppendLine($"Value: {item.Value}");
            if (!string.IsNullOrEmpty(item.RiskLevel)) sb.AppendLine($"Risk: {item.RiskLevel}");
            if (!string.IsNullOrEmpty(item.LlmSummary))     { sb.AppendLine(); sb.AppendLine(item.LlmSummary); }
            if (!string.IsNullOrEmpty(item.LlmExplanation)) { sb.AppendLine(); sb.AppendLine(item.LlmExplanation); }
            if (!string.IsNullOrEmpty(item.FailureReason))  { sb.AppendLine(); sb.AppendLine($"⚠️ {item.FailureReason}"); }
            txtDetail.Text = sb.ToString();

            if (!string.IsNullOrEmpty(item.UnifiedDiff))
            {
                ColorDiff(item.UnifiedDiff);
            }
            else if (!string.IsNullOrEmpty(item.OriginalSnippet) && !string.IsNullOrEmpty(item.ProposedSnippet))
            {
                txtDiff.Text = $"--- Original ---\n{item.OriginalSnippet}\n\n+++ Proposed +++\n{item.ProposedSnippet}";
            }
            else
            {
                txtDiff.Text = item.Status == SamFixStatus.ArchitectureOnly
                    ? "(Architecture-level issue — no direct code patch available)"
                    : "";
            }
        }

        private void ColorDiff(string diff)
        {
            txtDiff.Clear();
            foreach (string line in diff.Split('\n'))
            {
                Color c = line.StartsWith("+") ? Color.FromArgb(150, 255, 150) :
                          line.StartsWith("-") ? Color.FromArgb(255, 130, 130) :
                          line.StartsWith("@") ? Color.FromArgb(130, 180, 255) : FG;
                txtDiff.SelectionColor = c;
                txtDiff.AppendText(line + "\n");
            }
        }

        private void NavigateToItem(SamTodoItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !item.HasCodeLocation) return;
            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    if (dte == null) return;
                    var doc = dte.Documents.Open(item.FilePath);
                    if (doc?.Object("TextDocument") is EnvDTE.TextDocument td)
                    {
                        var pt = td.CreateEditPoint();
                        pt.MoveToLineAndOffset(Math.Max(1, item.LineNo), 1);
                        td.Selection.MoveToPoint(pt);
                    }
                });
                _lastNavigatedItemId = item.Id;
            }
            catch { }
        }

        private SamTodoItem GetSelectedItem()
        {
            if (gridView.SelectedRows.Count == 0) return null;
            return gridView.SelectedRows[0].DataBoundItem as SamTodoItem;
        }

        // ─── Utility ──────────────────────────────────────────────────────

        private void TrySetDefaultSourceRoot()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    string sol = dte?.Solution?.FullName;
                    if (!string.IsNullOrEmpty(sol))
                    {
                        string dir = Path.GetDirectoryName(sol);
                        Invoke((Action)(() => txtSourceRoot.Text = dir));
                    }
                });
            }
            catch { }
        }

        private string PromptForInput(string title, string prompt, string defaultValue)
        {
            using (var form = new Form
            {
                Text = title, Width = 480, Height = 140, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                BackColor = BG
            })
            {
                var lbl = new Label { Left = 10, Top = 12, Width = 440, Text = prompt, ForeColor = FG };
                var txt = new TextBox { Left = 10, Top = 32, Width = 440, Text = defaultValue, BackColor = BG2, ForeColor = FG };
                var ok  = new Button { Text = "OK",     Left = 280, Top = 64, Width = 80, DialogResult = DialogResult.OK };
                var can = new Button { Text = "Cancel", Left = 370, Top = 64, Width = 80, DialogResult = DialogResult.Cancel };
                form.Controls.AddRange(new Control[] { lbl, txt, ok, can });
                form.AcceptButton = ok; form.CancelButton = can;
                return form.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
            }
        }

        private Button CreateToolButton(string text, Color back)
        {
            var btn = new Button
            {
                Text = text, Height = 26, AutoSize = true, FlatStyle = FlatStyle.Flat,
                BackColor = back, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(2, 0, 2, 0), Padding = new Padding(6, 0, 6, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Button CreateActionButton(string text, Color back)
        {
            var btn = new Button
            {
                Text = text, Height = 24, AutoSize = true, FlatStyle = FlatStyle.Flat,
                BackColor = back, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(2, 0, 4, 0), Padding = new Padding(8, 0, 8, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }
    }
}
