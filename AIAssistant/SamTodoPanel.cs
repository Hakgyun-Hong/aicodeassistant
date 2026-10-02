using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// SAM Fix Assistant — Modern tool window for Samsung SAM Quality Metrics.
    ///
    /// Architecture:
    ///   - Left Sidebar: Categorized metrics (Overview, Code Fixes, Architecture) with count badges.
    ///   - Top Toolbar: Load File/URL, Analyze Category/Selected, Cancel, Retry, Root path.
    ///   - Center Table: DYNAMIC columns per selected metric (100% matching SAM report schema).
    ///   - Bottom Split: Detailed reasoning (left) + Syntax-highlighted Diff / Extracted File (right).
    ///   - Action Bar: Accept & Apply (auto-creates extracted files), Save Extracted File, Re-analyze, Preview.
    /// </summary>
    public sealed class SamTodoPanel : UserControl
    {
        // ─── Theme & Palette ────────────────────────────────────────────────
        private static readonly Color CLR_BG         = Color.FromArgb(22, 22, 30);
        private static readonly Color CLR_CARD       = Color.FromArgb(32, 34, 44);
        private static readonly Color CLR_SIDEBAR    = Color.FromArgb(26, 28, 38);
        private static readonly Color CLR_TEXT       = Color.FromArgb(225, 228, 235);
        private static readonly Color CLR_TEXT_DIM   = Color.FromArgb(135, 140, 160);
        private static readonly Color CLR_ACCENT     = Color.FromArgb(88, 138, 255);
        private static readonly Color CLR_BORDER     = Color.FromArgb(52, 56, 72);
        private static readonly Color CLR_DANGER     = Color.FromArgb(200, 60, 60);
        private static readonly Color CLR_INPUT      = Color.FromArgb(40, 42, 54);
        private static readonly Color CLR_SELECTED   = Color.FromArgb(38, 52, 78);

        private static readonly Font FNT_BODY   = new Font("Segoe UI", 9.5f);
        private static readonly Font FNT_SMALL  = new Font("Segoe UI", 8.5f);
        private static readonly Font FNT_TITLE  = new Font("Segoe UI Semibold", 10f);
        private static readonly Font FNT_MONO   = new Font("Cascadia Code", 9f);

        // ─── Metric Color Mapping ───────────────────────────────────────────
        private static readonly Dictionary<string, Color> MetricColors = new Dictionary<string, Color>
        {
            {"ALL",  Color.FromArgb(140, 160, 255)},
            {"CC",   Color.FromArgb(88, 138, 255)},   // blue
            {"NCS",  Color.FromArgb(60, 190, 210)},   // cyan
            {"DC",   Color.FromArgb(235, 160, 50)},   // orange
            {"GV",   Color.FromArgb(60, 190, 120)},   // green
            {"PP",   Color.FromArgb(160, 150, 180)},  // slate
            {"LOC",  Color.FromArgb(90, 210, 180)},   // teal
            {"GM",   Color.FromArgb(215, 75, 75)},    // red
            {"MCD",  Color.FromArgb(230, 90, 90)},    // coral
            {"CBO",  Color.FromArgb(170, 110, 230)},  // purple
            {"DepC", Color.FromArgb(210, 130, 60)},   // amber
        };

        private static readonly HashSet<string> CodeLevelMetrics =
            new HashSet<string> { "CC", "NCS", "DC", "GV", "PP", "LOC" };

        private static readonly Dictionary<string, string> MetricNames = new Dictionary<string, string>
        {
            {"ALL",  "All Metrics"},
            {"CC",   "Cyclomatic Complexity"},
            {"NCS",  "Nested Control Statements"},
            {"DC",   "Duplicate Code"},
            {"GV",   "Global Variable"},
            {"PP",   "Predefined Preprocessor"},
            {"LOC",  "Lines of Code"},
            {"GM",   "God Module"},
            {"MCD",  "Module Circular Dependency"},
            {"CBO",  "Coupling Between Objects"},
            {"DepC", "Dependency Complexity"},
        };

        private static readonly Dictionary<string, string> MetricGoals = new Dictionary<string, string>
        {
            {"ALL",  "All SAM Metric Violations in Solution"},
            {"CC",   "Target: CC ≤ 10  •  Guard clauses & helper extraction"},
            {"NCS",  "Target: Depth ≤ 3  •  Early returns & condition inversion"},
            {"DC",   "Target: Zero Duplicate Blocks  •  Shared helper extraction"},
            {"GV",   "Target: Zero Globals  •  Class/struct wrapper & accessor"},
            {"PP",   "Target: Minimal Macros  •  Replace with constexpr & enums"},
            {"LOC",  "Target: Within Threshold  •  Extract types/helpers to new file"},
            {"GM",   "Target: Cohesive Scope  •  Split into focused sub-modules"},
            {"MCD",  "Target: Zero Cycles  •  Dependency inversion & interfaces"},
            {"CBO",  "Target: Loose Coupling  •  Interface extraction & DIP"},
            {"DepC", "Target: Low Fan-Out  •  Consolidate module dependencies"},
        };

        // ─── Services ───────────────────────────────────────────────────────
        private readonly SamTodoStore     _store;
        private readonly SamAnalysisQueue _analysisQueue;

        // ─── UI Controls ────────────────────────────────────────────────────
        private Panel          pnlSidebar;
        private DataGridView   gridView;
        private RichTextBox    txtDetail;
        private RichTextBox    txtDiff;
        private Label          lblStatus;
        private Label          lblCategoryTitle;
        private TextBox        txtSourceRoot;
        private TextBox        txtSearch;
        private Button         btnSaveExtracted;

        // ─── State ──────────────────────────────────────────────────────────
        private string   _selectedMetric = null;  // null = ALL
        private Guid     _lastNavigatedItemId = Guid.Empty;
        private bool     _suppressNavigation  = false;
        private Timer    _debounceTimer;

        // ═══════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ═══════════════════════════════════════════════════════════════════

        public SamTodoPanel()
        {
            _store         = SamTodoStore.Instance;
            _analysisQueue = new SamAnalysisQueue(_store, maxParallel: 3);

            _debounceTimer = new Timer { Interval = 150 };
            _debounceTimer.Tick += (s, e) => { _debounceTimer.Stop(); DoRefreshGrid(); };

            _store.Changed += () =>
            {
                if (InvokeRequired) BeginInvoke((Action)RefreshAll);
                else RefreshAll();
            };

            _analysisQueue.ProgressChanged += (done, total) =>
            {
                string msg = $"✨ Analyzing... {done}/{total}";
                if (InvokeRequired) BeginInvoke((Action)(() => lblStatus.Text = msg));
                else lblStatus.Text = msg;
            };

            DoubleBuffered = true;
            BuildUI();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            BackColor = CLR_BG;
            Padding = Padding.Empty;

            // ── Top Toolbar ──
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                BackColor = CLR_SIDEBAR, Padding = new Padding(8, 5, 8, 5),
                WrapContents = false
            };

            var btnLoad    = MakeBtn("📂 Load File",        CLR_ACCENT);
            var btnUrl     = MakeBtn("🌐 Load URL",         Color.FromArgb(60, 105, 75));
            var btnAnalyze = MakeBtn("⚡ Analyze Category",  Color.FromArgb(85, 65, 130));
            var btnSelAna  = MakeBtn("🎯 Analyze Selected",  Color.FromArgb(65, 85, 130));
            var btnCancel  = MakeBtn("⏹ Cancel",            CLR_DANGER);
            var btnClear   = MakeBtn("🧹 Clear",             CLR_BORDER);
            var btnRetry   = MakeBtn("🔄 Retry Failed",      Color.FromArgb(95, 65, 35));

            btnLoad.Click    += BtnLoadFile_Click;
            btnUrl.Click     += BtnLoadUrl_Click;
            btnAnalyze.Click += BtnAnalyzeAll_Click;
            btnSelAna.Click  += BtnAnalyzeSelected_Click;
            btnCancel.Click  += BtnCancel_Click;
            btnClear.Click   += (s, e) => { _store.Clear(); RefreshAll(); lblStatus.Text = "Cleared all items."; };
            btnRetry.Click   += BtnRetryFailed_Click;

            toolbar.Controls.AddRange(new Control[]
                { btnLoad, btnUrl, btnAnalyze, btnSelAna, btnCancel, btnClear, btnRetry });

            // ── Source Root & Search Bar ──
            var rootBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 30,
                BackColor = CLR_SIDEBAR, Padding = new Padding(8, 2, 8, 4),
                WrapContents = false
            };
            rootBar.Controls.Add(MakeLabel("Source Root:", 3));

            txtSourceRoot = new TextBox
            {
                Width = 380, BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                Font = FNT_SMALL, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 6, 0)
            };
            txtSourceRoot.TextChanged += (s, e) =>
            {
                string r = txtSourceRoot.Text.Trim();
                if (!string.IsNullOrEmpty(r) && (Directory.Exists(r) || File.Exists(r)))
                {
                    int res = ReResolveAllFilePaths(r);
                    lblStatus.Text = $"Source root updated: {res}/{_store.Items.Count} paths resolved.";
                    RefreshGrid();
                }
            };
            TrySetDefaultSourceRoot();
            rootBar.Controls.Add(txtSourceRoot);

            var btnBrowse = MakeBtn("…", CLR_BORDER);
            btnBrowse.Width = 28; btnBrowse.Padding = new Padding(0);
            btnBrowse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Select Solution/Project Source Root" })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        txtSourceRoot.Text = dlg.SelectedPath;
                        int res = ReResolveAllFilePaths(dlg.SelectedPath);
                        lblStatus.Text = $"Source root set: {res}/{_store.Items.Count} paths resolved.";
                        RefreshGrid();
                    }
                }
            };
            rootBar.Controls.Add(btnBrowse);

            rootBar.Controls.Add(MakeLabel("    🔍", 3));
            txtSearch = new TextBox
            {
                Width = 170, BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                Font = FNT_SMALL, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 0, 0)
            };
            txtSearch.TextChanged += (s, e) => RefreshGrid();
            rootBar.Controls.Add(txtSearch);

            // ── Bottom Status Bar ──
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom, Height = 24,
                Text = "Load a SAM HTML report to begin.",
                BackColor = Color.FromArgb(18, 18, 24), ForeColor = CLR_TEXT_DIM,
                Font = FNT_SMALL, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            // ── Action Bar (above status bar) ──
            var actionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 36,
                BackColor = CLR_SIDEBAR, Padding = new Padding(8, 5, 8, 5),
                WrapContents = false
            };
            var btnAccept    = MakeBtn("✅ Accept & Apply",      Color.FromArgb(40, 100, 45));
            var btnReject    = MakeBtn("❌ Reject",               Color.FromArgb(100, 45, 45));
            btnSaveExtracted = MakeBtn("📁 Save Extracted File", Color.FromArgb(40, 85, 120));
            var btnReana     = MakeBtn("🔄 Re-analyze",           Color.FromArgb(55, 65, 95));
            var btnPreview   = MakeBtn("🔎 Preview Diff",         Color.FromArgb(55, 55, 75));

            btnAccept.Click        += BtnAccept_Click;
            btnReject.Click        += BtnReject_Click;
            btnSaveExtracted.Click += BtnSaveExtracted_Click;
            btnReana.Click         += BtnReanalyze_Click;
            btnPreview.Click       += BtnPreview_Click;

            btnSaveExtracted.Visible = false;

            actionBar.Controls.AddRange(new Control[]
                { btnAccept, btnReject, btnSaveExtracted, btnReana, btnPreview });

            // ══ MAIN AREA: Sidebar | Content ══

            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                BackColor = CLR_BORDER, Panel1MinSize = 130, Panel2MinSize = 350
            };
            bool splitSet = false;
            mainSplit.Layout += (s, e) =>
            {
                if (!splitSet && mainSplit.Width > 200)
                { mainSplit.SplitterDistance = 165; splitSet = true; }
            };

            // ── Left: Sidebar ──
            pnlSidebar = new Panel
            {
                Dock = DockStyle.Fill, BackColor = CLR_SIDEBAR,
                AutoScroll = true, Padding = new Padding(0, 4, 0, 6)
            };
            BuildSidebar();
            mainSplit.Panel1.Controls.Add(pnlSidebar);

            // ── Right: Content (Grid + Detail) ──
            var contentSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                BackColor = CLR_BORDER, Panel1MinSize = 100, Panel2MinSize = 120
            };
            bool cSplitSet = false;
            contentSplit.Layout += (s, e) =>
            {
                if (!cSplitSet && contentSplit.Height > 200)
                { contentSplit.SplitterDistance = (int)(contentSplit.Height * 0.46); cSplitSet = true; }
            };

            // Category title header
            lblCategoryTitle = new Label
            {
                Dock = DockStyle.Top, Height = 32,
                Text = "  ★ ALL METRICS", Font = FNT_TITLE,
                ForeColor = CLR_ACCENT, BackColor = CLR_CARD,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            // Grid
            gridView = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = CLR_BG, ForeColor = CLR_TEXT,
                GridColor = CLR_BORDER, BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                Font = FNT_BODY,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = CLR_BG, ForeColor = CLR_TEXT,
                    SelectionBackColor = CLR_SELECTED, SelectionForeColor = Color.White
                },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(28, 30, 40)
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = CLR_CARD, ForeColor = Color.FromArgb(185, 190, 210),
                    Font = FNT_SMALL
                },
                RowTemplate = { Height = 24 }
            };
            gridView.SelectionChanged += GridView_SelectionChanged;
            SetupGridColumns();

            var gridPanel = new Panel { Dock = DockStyle.Fill };
            gridPanel.Controls.Add(gridView);
            gridPanel.Controls.Add(lblCategoryTitle);
            contentSplit.Panel1.Controls.Add(gridPanel);

            // Detail panel (reasoning + diff)
            var detailSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                BackColor = CLR_BORDER, Panel1MinSize = 140, Panel2MinSize = 180
            };
            bool dSplitSet = false;
            detailSplit.Layout += (s, e) =>
            {
                if (!dSplitSet && detailSplit.Width > 200)
                { detailSplit.SplitterDistance = (int)(detailSplit.Width * 0.38); dSplitSet = true; }
            };

            txtDetail = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = CLR_CARD, ForeColor = CLR_TEXT,
                Font = FNT_BODY, ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            txtDiff = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 18, 26), ForeColor = CLR_TEXT,
                Font = FNT_MONO, ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Both, WordWrap = false
            };
            detailSplit.Panel1.Controls.Add(txtDetail);
            detailSplit.Panel2.Controls.Add(txtDiff);
            contentSplit.Panel2.Controls.Add(detailSplit);

            mainSplit.Panel2.Controls.Add(contentSplit);

            // Assemble main layout
            Controls.Add(mainSplit);
            Controls.Add(actionBar);
            Controls.Add(rootBar);
            Controls.Add(toolbar);
            Controls.Add(lblStatus);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  SIDEBAR: Grouped by Code Fixes vs Architecture
        // ═══════════════════════════════════════════════════════════════════

        private struct SidebarEntry
        {
            public string Key;
            public bool   IsHeader;
            public string Label;
            public SidebarEntry(string k, bool isHdr, string lbl) { Key = k; IsHeader = isHdr; Label = lbl; }
        }

        private static readonly SidebarEntry[] SidebarStructure = new[]
        {
            new SidebarEntry("ALL",   false, "ALL METRICS"),
            new SidebarEntry("",      true,  "⚡ CODE FIXES"),
            new SidebarEntry("CC",    false, "CC (Cyclomatic)"),
            new SidebarEntry("NCS",   false, "NCS (Nesting)"),
            new SidebarEntry("DC",    false, "DC (Duplicate)"),
            new SidebarEntry("GV",    false, "GV (Globals)"),
            new SidebarEntry("PP",    false, "PP (Macros)"),
            new SidebarEntry("LOC",   false, "LOC (Lines of Code)"),
            new SidebarEntry("",      true,  "🏛️ ARCHITECTURE"),
            new SidebarEntry("GM",    false, "GM (God Module)"),
            new SidebarEntry("MCD",   false, "MCD (Circular Dep)"),
            new SidebarEntry("CBO",   false, "CBO (Coupling)"),
            new SidebarEntry("DepC",  false, "DepC (Dependency)"),
        };

        private void BuildSidebar()
        {
            pnlSidebar.SuspendLayout();
            pnlSidebar.Controls.Clear();

            int y = 4;
            int w = pnlSidebar.Width > 0 ? pnlSidebar.Width - 2 : 160;

            foreach (var entry in SidebarStructure)
            {
                if (entry.IsHeader)
                {
                    y += 4;
                    var hdr = new Label
                    {
                        Text = $" {entry.Label}",
                        Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                        ForeColor = Color.FromArgb(145, 150, 175),
                        BackColor = CLR_SIDEBAR,
                        Location = new Point(4, y), Width = w - 8, Height = 20,
                        TextAlign = ContentAlignment.MiddleLeft
                    };
                    pnlSidebar.Controls.Add(hdr);
                    y += 22;
                    continue;
                }

                string key = entry.Key;
                int count = key == "ALL" ? _store.Items.Count : _store.Items.Count(x => x.Metric == key);
                bool isSelected = (_selectedMetric == null && key == "ALL") || _selectedMetric == key;

                Color dotColor;
                if (!MetricColors.TryGetValue(key, out dotColor)) dotColor = CLR_ACCENT;

                var btn = new Panel
                {
                    Location = new Point(0, y), Width = w, Height = 28,
                    BackColor = isSelected ? CLR_SELECTED : CLR_SIDEBAR,
                    Cursor = Cursors.Hand, Tag = key
                };

                btn.Paint += (s, e) =>
                {
                    var p = (Panel)s;
                    string k = p.Tag as string;
                    bool sel = (_selectedMetric == null && k == "ALL") || _selectedMetric == k;
                    bool code = CodeLevelMetrics.Contains(k);

                    Color dc;
                    if (!MetricColors.TryGetValue(k, out dc)) dc = CLR_ACCENT;

                    p.BackColor = sel ? CLR_SELECTED : CLR_SIDEBAR;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                    // Left accent bar
                    if (sel)
                    {
                        using (var brush = new SolidBrush(dc))
                            e.Graphics.FillRectangle(brush, 0, 2, 3, p.Height - 4);
                    }

                    // Dot symbol: ★ ALL, ● Code, ◆ Arch
                    string dot = (k == "ALL") ? "★" : (code ? "●" : "◆");
                    using (var brush = new SolidBrush(dc))
                    using (var font = new Font("Segoe UI", 8f))
                        e.Graphics.DrawString(dot, font, brush, 8, 6);

                    // Name
                    string name = k;
                    using (var brush = new SolidBrush(sel ? Color.White : CLR_TEXT))
                    using (var font = new Font("Segoe UI", 8.5f, sel ? FontStyle.Bold : FontStyle.Regular))
                        e.Graphics.DrawString(name, font, brush, 24, 5);

                    // Count badge
                    int cnt = k == "ALL" ? _store.Items.Count : _store.Items.Count(x => x.Metric == k);
                    if (cnt > 0)
                    {
                        string cntStr = cnt.ToString();
                        using (var font = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                        {
                            var sz = e.Graphics.MeasureString(cntStr, font);
                            float bx = p.Width - sz.Width - 10;
                            float by = 6;
                            using (var bg = new SolidBrush(Color.FromArgb(45, dc.R, dc.G, dc.B)))
                                e.Graphics.FillRoundedRectangle(bg, bx - 4, by - 1, sz.Width + 8, 16, 4);
                            using (var brush = new SolidBrush(dc))
                                e.Graphics.DrawString(cntStr, font, brush, bx, by);
                        }
                    }
                };

                string capturedKey = key;
                btn.Click += (s, e) =>
                {
                    _selectedMetric = (capturedKey == "ALL") ? null : capturedKey;
                    UpdateCategoryTitle();
                    SetupGridColumns();
                    BuildSidebar();
                    RefreshGrid();
                };

                MetricNames.TryGetValue(key, out string tt);
                new ToolTip().SetToolTip(btn, tt ?? key);

                pnlSidebar.Controls.Add(btn);
                y += 29;
            }

            pnlSidebar.ResumeLayout();
        }

        private void UpdateCategoryTitle()
        {
            string key = _selectedMetric ?? "ALL";
            MetricNames.TryGetValue(key, out string fullName);
            MetricGoals.TryGetValue(key, out string goal);

            int count = key == "ALL" ? _store.Items.Count : _store.Items.Count(x => x.Metric == key);
            bool isCode = CodeLevelMetrics.Contains(key);
            string symbol = (key == "ALL") ? "★" : (isCode ? "●" : "◆");

            lblCategoryTitle.Text = $"  {symbol} {key} — {fullName}  ({count} items)    •    {goal}";

            Color c;
            lblCategoryTitle.ForeColor = MetricColors.TryGetValue(key, out c) ? c : CLR_ACCENT;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  GRID: Dynamic Columns Matching SAM Quality Report Schema
        // ═══════════════════════════════════════════════════════════════════

        private void SetupGridColumns()
        {
            gridView.Columns.Clear();

            switch (_selectedMetric)
            {
                case "CC":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Value",         "CC",          50);
                    AddCol("Entity",        "Function",    140);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Summary / Fix Suggestion", 250);
                    AddCol("RiskLevel",     "Risk",        60);
                    break;

                case "NCS":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Value",         "Depth",       55);
                    AddCol("Entity",        "Function",    120);
                    AddCol("Signature",     "Signature",   150);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Summary / Fix Suggestion", 250);
                    break;

                case "DC":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("LineDisplay",   "Lines",       75);
                    AddCol("LineCount",     "Count",       55);
                    AddCol("Token",         "Tokens",      60);
                    AddCol("BlockKey",      "Block Key",   100);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Shared Helper Fix", 250);
                    break;

                case "GV":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Entity",        "Variable",    120);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("AccessingFunc", "Accessing Functions", 160);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Encapsulation Fix", 250);
                    break;

                case "PP":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Name",          "Macro Name",  120);
                    AddCol("MacroType",     "Type",        80);
                    AddCol("LineDisplay",   "Lines",       70);
                    AddCol("PP_LOC",        "PP LOC",      65);
                    AddCol("MacroCount",    "Count",       55);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Replacement", 250);
                    break;

                case "LOC":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Value",         "LOC",         60);
                    AddCol("Class",         "Class",       120);
                    AddCol("Package",       "Package",     110);
                    AddCol("ClassType",     "Type",        70);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Decomposition Fix", 250);
                    break;

                case "GM":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("BLOC",          "BLOC",        60);
                    AddCol("FunctionCnt",   "Funcs",       60);
                    AddCol("BLOCxFunctionCnt", "BLOC×Func", 85);
                    AddCol("Module",        "Module",      120);
                    AddCol("ShortFilePath", "Path",        140);
                    AddCol("LlmSummary",    "Modularization Plan", 250);
                    break;

                case "MCD":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("FromModule",    "From Module", 130);
                    AddCol("ToModule",      "To Module",   130);
                    AddCol("RelationCount", "Relations",   75);
                    AddCol("Mark",          "Mark",        60);
                    AddCol("LlmSummary",    "Decoupling Plan", 250);
                    break;

                case "CBO":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Value",         "Coupling",    65);
                    AddCol("Class",         "Class",       120);
                    AddCol("Package",       "Package",     110);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LlmSummary",    "Interface Extraction", 250);
                    break;

                case "DepC":
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Complexity",    "Complexity",  80);
                    AddCol("ModuleMaxFanOut","Max Fan-out", 85);
                    AddCol("ParentModule",  "Parent",      120);
                    AddCol("ShortFilePath", "Path",        130);
                    AddCol("LlmSummary",    "Consolidation Plan", 250);
                    break;

                default: // ALL
                    AddCol("StatusText",    "Status",      85);
                    AddCol("Metric",        "Metric",      60);
                    AddCol("Value",         "Value",       55);
                    AddCol("Entity",        "Entity",      120);
                    AddCol("ShortFilePath", "File",        130);
                    AddCol("LineDisplay",   "Line",        60);
                    AddCol("LlmSummary",    "Summary / Fix", 250);
                    AddCol("RiskLevel",     "Risk",        60);
                    break;
            }

            void AddCol(string prop, string header, int width)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    DataPropertyName = prop,
                    HeaderText = header,
                    Width = width,
                    MinimumWidth = 30
                };
                if (prop == "LlmSummary")
                    col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                gridView.Columns.Add(col);
            }
        }

        private void RefreshAll()
        {
            BuildSidebar();
            UpdateCategoryTitle();
            RefreshGrid();
        }

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

            UpdateStatusBar();

            // Refresh detail view for selected item
            ShowDetail(GetSelectedItem());
        }

        private IEnumerable<SamTodoItem> GetFilteredItems()
        {
            var items = (IEnumerable<SamTodoItem>)_store.Items;

            if (!string.IsNullOrEmpty(_selectedMetric))
                items = items.Where(x => x.Metric == _selectedMetric);

            string query = txtSearch?.Text?.Trim();
            if (!string.IsNullOrEmpty(query))
                items = items.Where(x =>
                    (x.EffectiveRelativePath ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Entity ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Class ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.LlmSummary ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Metric ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            return items;
        }

        private void UpdateStatusBar()
        {
            var all = _store.Items;
            int pending   = all.Count(x => x.Status == SamFixStatus.PendingAnalysis);
            int analyzing = all.Count(x => x.Status == SamFixStatus.Analyzing);
            int suggested = all.Count(x => x.Status == SamFixStatus.Suggested);
            int accepted  = all.Count(x => x.Status == SamFixStatus.Accepted);
            int rejected  = all.Count(x => x.Status == SamFixStatus.Rejected);
            int failed    = all.Count(x => x.Status == SamFixStatus.Failed);
            int arch      = all.Count(x => x.Status == SamFixStatus.ArchitectureOnly);

            lblStatus.Text = $"Total: {all.Count}  " +
                $"⏳{pending}  🔄{analyzing}  💡{suggested}  ✅{accepted}  ❌{rejected}  ⚠️{failed}  🏛️{arch}";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PATH RESOLUTION
        // ═══════════════════════════════════════════════════════════════════

        private int ReResolveAllFilePaths(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return 0;
            int resolved = 0;
            foreach (var item in _store.Items)
            {
                string rel = item.EffectiveRelativePath;
                if (string.IsNullOrEmpty(rel)) continue;

                string abs = ResolveFilePath(root, rel);
                if (!string.IsNullOrEmpty(abs))
                {
                    item.FilePath = abs;
                    if (File.Exists(abs) && string.IsNullOrEmpty(item.OriginalFileHash))
                        item.OriginalFileHash = WarningCollector.ComputeSha256(abs);
                    resolved++;
                }
            }
            return resolved;
        }

        private static string ResolveFilePath(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
                return null;

            string norm = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                      .Replace('\\', Path.DirectorySeparatorChar)
                                      .TrimStart(Path.DirectorySeparatorChar);

            // 1. Direct combine
            string direct = Path.Combine(root, norm);
            if (File.Exists(direct)) return direct;

            // 2. If root is a file (e.g. .sln or .csproj)
            if (File.Exists(root))
            {
                string dir = Path.GetDirectoryName(root);
                string cand = Path.Combine(dir, norm);
                if (File.Exists(cand)) return cand;
            }

            if (!Directory.Exists(root)) return null;

            // 3. Search matching filename within root
            string fileName = Path.GetFileName(norm);
            try
            {
                var matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
                if (matches.Length == 1) return matches[0];
                if (matches.Length > 1)
                {
                    foreach (var m in matches)
                    {
                        if (m.EndsWith(norm, StringComparison.OrdinalIgnoreCase))
                            return m;
                    }
                    return matches[0];
                }
            }
            catch { }

            return null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TOOLBAR ACTIONS
        // ═══════════════════════════════════════════════════════════════════

        private void BtnLoadFile_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title  = "Open SAM Quality Report HTML",
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
                "Enter SAM report URL (e.g. https://.../result/sam.html):", "https://");
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
                    if (!string.IsNullOrEmpty(root))
                    {
                        string abs = ResolveFilePath(root, item.EffectiveRelativePath);
                        if (!string.IsNullOrEmpty(abs))
                        {
                            item.FilePath = abs;
                            if (File.Exists(abs))
                                item.OriginalFileHash = WarningCollector.ComputeSha256(abs);
                            resolved++;
                        }
                    }
                    _store.AddOrUpdate(item);
                }

                RefreshAll();
                lblStatus.Text = $"Loaded {items.Count} items ({resolved} file paths resolved).";
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
                .Where(x => _selectedMetric == null || x.Metric == _selectedMetric)
                .ToList();

            if (pending.Count == 0)
            {
                lblStatus.Text = "ℹ️ No pending items to analyze in this category.";
                return;
            }

            lblStatus.Text = $"⚡ Analyzing {pending.Count} item(s)...";
            foreach (var item in pending)
                _analysisQueue.Enqueue(item);

            await _analysisQueue.ProcessAllAsync();

            try { Invoke((Action)(() => RefreshAll())); }
            catch { }
        }

        private async void BtnAnalyzeSelected_Click(object sender, EventArgs e)
        {
            if (gridView.SelectedRows.Count == 0)
            { lblStatus.Text = "⚠️ No rows selected."; return; }

            var selected = new List<SamTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as SamTodoItem;
                if (item == null || item.Status == SamFixStatus.Analyzing || item.Status == SamFixStatus.Accepted)
                    continue;
                selected.Add(item);
            }

            if (selected.Count == 0)
            { lblStatus.Text = "ℹ️ Selected items already analyzed or accepted."; return; }

            lblStatus.Text = $"🎯 Analyzing {selected.Count} selected item(s)...";
            _analysisQueue.ResetAndEnqueue(selected);
            RefreshGrid();
            await _analysisQueue.ProcessAllAsync();

            try { Invoke((Action)(() => RefreshAll())); }
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
            RefreshAll();
            lblStatus.Text = reverted > 0 ? $"⏹ Cancelled. ({reverted} reverted)" : "⏹ Cancelled.";
        }

        private async void BtnRetryFailed_Click(object sender, EventArgs e)
        {
            var failed = _store.Items
                .Where(x => x.Status == SamFixStatus.Failed || x.Status == SamFixStatus.Stale)
                .Where(x => _selectedMetric == null || x.Metric == _selectedMetric)
                .ToList();

            if (failed.Count == 0) { lblStatus.Text = "ℹ️ No failed/stale items in this category."; return; }

            lblStatus.Text = $"🔄 Retrying {failed.Count} item(s)...";
            _analysisQueue.ResetAndEnqueue(failed);
            RefreshGrid();
            await _analysisQueue.ProcessAllAsync();

            try { Invoke((Action)(() => RefreshAll())); }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ACTION BUTTONS: Accept & Apply, Save File, Diff Preview
        // ═══════════════════════════════════════════════════════════════════

        private void BtnAccept_Click(object sender, EventArgs e)
        {
            var targets = new List<SamTodoItem>();
            foreach (DataGridViewRow row in gridView.SelectedRows)
            {
                var item = row.DataBoundItem as SamTodoItem;
                if (item != null && item.Status == SamFixStatus.Suggested && item.HasCodeLocation)
                    targets.Add(item);
            }

            if (targets.Count == 0)
            {
                lblStatus.Text = "ℹ️ No applicable suggested items selected.";
                return;
            }

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
                // Step 1: Auto-create extracted file if suggested (e.g. for LOC or component extraction)
                if (item.HasExtractedFile && !string.IsNullOrEmpty(item.FilePath))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(item.FilePath);
                        string newFilePath = Path.Combine(dir, item.ExtractedFileName);
                        File.WriteAllText(newFilePath, item.ExtractedFileContent, Encoding.UTF8);
                        Log($"[Apply] Auto-created extracted file: {newFilePath}");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        lastError = $"Failed to create {item.ExtractedFileName}: {ex.Message}";
                        continue;
                    }
                }

                // Step 2: Apply code replacement in current file
                var bridge = new WarningTodoItem
                {
                    FilePath         = item.FilePath,
                    Line             = item.LineNo,
                    OriginalSnippet  = item.OriginalSnippet,
                    ProposedSnippet  = item.ProposedSnippet,
                    OriginalFileHash = item.OriginalFileHash,
                    Code             = item.Metric,
                    Status           = WarningFixStatus.Suggested
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

            lblStatus.Text = fail == 0
                ? (ok == 1 ? "✅ Fix applied successfully." : $"✅ {ok} fixes applied successfully.")
                : $"⚠️ {ok} applied, {fail} failed: {lastError}";

            RefreshAll();
        }

        private void BtnSaveExtracted_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null || !item.HasExtractedFile)
            {
                lblStatus.Text = "ℹ️ Selected item has no extracted file.";
                return;
            }

            string defaultDir = !string.IsNullOrEmpty(item.FilePath)
                ? Path.GetDirectoryName(item.FilePath)
                : txtSourceRoot.Text.Trim();

            using (var dlg = new SaveFileDialog
            {
                Title = "Save Extracted File",
                FileName = item.ExtractedFileName,
                InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : null,
                Filter = "All Files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(dlg.FileName, item.ExtractedFileContent, Encoding.UTF8);
                    lblStatus.Text = $"💾 Saved extracted file: {Path.GetFileName(dlg.FileName)}";
                }
            }
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
            RefreshAll();
            lblStatus.Text = count == 1 ? "❌ Rejected." : $"❌ Rejected {count} item(s).";
        }

        private async void BtnReanalyze_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;
            lblStatus.Text = $"🔄 Re-analyzing {item.Metric} in {item.ShortFilePath}...";
            await _analysisQueue.ReanalyzeAsync(item);
            try
            {
                Invoke((Action)(() => { RefreshAll(); ShowDetail(item); }));
            }
            catch { }
        }

        private void BtnPreview_Click(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            if (item == null) return;

            string orig = item.OriginalSnippet ?? "(no original snippet)";
            var propSb = new StringBuilder();
            if (!string.IsNullOrEmpty(item.ProposedSnippet))
            {
                propSb.AppendLine("=== Current File Replacement ===");
                propSb.AppendLine(item.ProposedSnippet);
            }
            if (item.HasExtractedFile)
            {
                propSb.AppendLine();
                propSb.AppendLine($"=== New File: {item.ExtractedFileName} ===");
                propSb.AppendLine(item.ExtractedFileContent);
            }
            if (!string.IsNullOrEmpty(item.LlmExplanation))
            {
                propSb.AppendLine();
                propSb.AppendLine("=== Explanation ===");
                propSb.AppendLine(item.LlmExplanation);
            }

            var dlg = new CodeReviewDialog(
                $"SAM Fix: {item.Metric} ({item.MetricFullName})",
                orig,
                propSb.ToString());
            dlg.ShowDialog();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  GRID SELECTION & DETAIL VIEW
        // ═══════════════════════════════════════════════════════════════════

        private void GridView_SelectionChanged(object sender, EventArgs e)
        {
            var item = GetSelectedItem();
            ShowDetail(item);

            if (_suppressNavigation) return;
            if (item != null && item.Id == _lastNavigatedItemId) return;
            if (item != null && item.HasCodeLocation && !string.IsNullOrEmpty(item.FilePath))
                NavigateToItem(item);
        }

        private void ShowDetail(SamTodoItem item)
        {
            if (item == null)
            {
                txtDetail.Clear();
                txtDiff.Clear();
                btnSaveExtracted.Visible = false;
                return;
            }

            btnSaveExtracted.Visible = item.HasExtractedFile;

            // 1. Text detail description
            var sb = new StringBuilder();
            bool isCode = CodeLevelMetrics.Contains(item.Metric);
            string typeTag = isCode ? "CODE FIX (Direct Patch)" : "ARCHITECTURE (Decomposition Plan)";

            sb.AppendLine($"[{item.Metric}] {item.MetricFullName}  —  {item.StatusText}");
            sb.AppendLine($"Category: {typeTag}");
            sb.AppendLine($"File: {item.EffectiveRelativePath}   Line: {item.LineDisplay}");
            if (!string.IsNullOrEmpty(item.Entity))    sb.AppendLine($"Entity: {item.Entity}");
            if (item.Value > 0)                         sb.AppendLine($"Value: {item.Value}");
            if (!string.IsNullOrEmpty(item.RiskLevel)) sb.AppendLine($"Risk: {item.RiskLevel}");

            if (!string.IsNullOrEmpty(item.LlmSummary))
            {
                sb.AppendLine();
                sb.AppendLine($"📝 {item.LlmSummary}");
            }

            if (item.HasExtractedFile)
            {
                sb.AppendLine();
                sb.AppendLine($"📁 PROPOSED NEW FILE: {item.ExtractedFileName}");
                sb.AppendLine($"   → Accept & Apply will automatically create this file in the directory and insert #include.");
            }

            if (!string.IsNullOrEmpty(item.LlmExplanation))
            {
                sb.AppendLine();
                sb.AppendLine(item.LlmExplanation);
            }

            if (!string.IsNullOrEmpty(item.FailureReason))
            {
                sb.AppendLine();
                sb.AppendLine($"⚠️ Failure: {item.FailureReason}");
            }

            txtDetail.Text = sb.ToString();

            // 2. Diff & code preview
            if (!string.IsNullOrEmpty(item.UnifiedDiff))
            {
                ColorDiff(item.UnifiedDiff);
                if (item.HasExtractedFile)
                {
                    txtDiff.AppendText("\n\n" +
                        "====================================================\n" +
                        $"📁 EXTRACTED NEW FILE: {item.ExtractedFileName}\n" +
                        "====================================================\n\n" +
                        item.ExtractedFileContent);
                }
            }
            else if (!string.IsNullOrEmpty(item.OriginalSnippet) && !string.IsNullOrEmpty(item.ProposedSnippet))
            {
                var diffSb = new StringBuilder();
                diffSb.AppendLine($"--- Original ({item.ShortFilePath}) ---");
                diffSb.AppendLine(item.OriginalSnippet);
                diffSb.AppendLine();
                diffSb.AppendLine($"+++ Proposed ({item.ShortFilePath}) +++");
                diffSb.AppendLine(item.ProposedSnippet);

                if (item.HasExtractedFile)
                {
                    diffSb.AppendLine();
                    diffSb.AppendLine("====================================================");
                    diffSb.AppendLine($"📁 EXTRACTED NEW FILE: {item.ExtractedFileName}");
                    diffSb.AppendLine("====================================================");
                    diffSb.AppendLine(item.ExtractedFileContent);
                }

                txtDiff.Text = diffSb.ToString();
            }
            else if (item.HasExtractedFile)
            {
                txtDiff.Text =
                    "====================================================\n" +
                    $"📁 EXTRACTED NEW FILE: {item.ExtractedFileName}\n" +
                    "====================================================\n\n" +
                    item.ExtractedFileContent;
            }
            else if (item.Status == SamFixStatus.ArchitectureOnly)
            {
                txtDiff.Text =
                    "🏛️ Architecture-Level Item\n\n" +
                    "Review the concrete refactoring plan and class decomposition in the left panel.\n" +
                    "Step-by-step instructions with recommended files and includes are provided.";
            }
            else
            {
                txtDiff.Clear();
            }
        }

        private void ColorDiff(string diff)
        {
            txtDiff.Clear();
            foreach (string line in diff.Split('\n'))
            {
                Color c = line.StartsWith("+") ? Color.FromArgb(150, 255, 150) :
                          line.StartsWith("-") ? Color.FromArgb(255, 130, 130) :
                          line.StartsWith("@") ? Color.FromArgb(130, 180, 255) : CLR_TEXT;
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
                    var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    if (dte == null || !File.Exists(item.FilePath)) return;

                    var doc = dte.Documents.Open(item.FilePath);
                    var td  = doc?.Selection as EnvDTE.TextSelection;
                    if (td != null && item.LineNo > 0)
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

        // ═══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════════

        private void TrySetDefaultSourceRoot()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    string sol = dte?.Solution?.FullName;
                    if (!string.IsNullOrEmpty(sol))
                    {
                        string dir = Path.GetDirectoryName(sol);
                        Invoke((Action)(() =>
                        {
                            txtSourceRoot.Text = dir;
                            ReResolveAllFilePaths(dir);
                        }));
                    }
                });
            }
            catch { }
        }

        private string PromptForInput(string title, string prompt, string defaultValue)
        {
            using (var form = new Form
            {
                Text = title, Width = 500, Height = 140, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                BackColor = CLR_BG
            })
            {
                var lbl = new Label { Left = 12, Top = 12, Width = 460, Text = prompt, ForeColor = CLR_TEXT };
                var txt = new TextBox { Left = 12, Top = 34, Width = 460, Text = defaultValue, BackColor = CLR_CARD, ForeColor = CLR_TEXT };
                var ok  = new Button { Text = "OK",     Left = 300, Top = 68, Width = 80, DialogResult = DialogResult.OK };
                var can = new Button { Text = "Cancel", Left = 390, Top = 68, Width = 80, DialogResult = DialogResult.Cancel };
                form.Controls.AddRange(new Control[] { lbl, txt, ok, can });
                form.AcceptButton = ok; form.CancelButton = can;
                return form.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
            }
        }

        private static Button MakeBtn(string text, Color bg)
        {
            var btn = new Button
            {
                Text = text, Height = 26, AutoSize = true,
                FlatStyle = FlatStyle.Flat, BackColor = bg,
                ForeColor = Color.White, Font = FNT_SMALL,
                Margin = new Padding(2, 0, 2, 0), Padding = new Padding(6, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static Label MakeLabel(string text, int topPad) =>
            new Label
            {
                Text = text, AutoSize = true, ForeColor = CLR_TEXT_DIM,
                Font = FNT_SMALL, Padding = new Padding(0, topPad, 2, 0)
            };

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][SamPanel] {msg}");
    }

    // ─── GDI+ Extension for Rounded Rectangle ────────────────────────────

    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush,
            float x, float y, float w, float h, int r)
        {
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, r * 2, r * 2, 180, 90);
                path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}
