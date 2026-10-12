using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// WPF Auto Manual Generator — Trupeer/StepHow-inspired card-based editor.
    ///
    /// Design philosophy:
    ///   - Each captured screen is a "Step Card" with screenshot + callout overlay + description
    ///   - Cards are vertically scrollable in a single-column flow
    ///   - Callouts are dynamically editable (insert/delete/toggle per card)
    ///   - Clean dark card aesthetic with rounded borders and subtle accents
    ///   - Inline text editing (click to edit title/description)
    ///   - Context actions per card (regenerate, move, delete, callout editor)
    ///
    /// Capture modes:
    ///   📸 Current Screen — single snapshot of the selected window
    ///   🌐 Full App       — auto-crawl all windows + Tab/Menu/Tree/List navigation
    /// </summary>
    public sealed class ManualEditorPanel : UserControl
    {
        // ─── Theme: dark card design inspired by Trupeer/StepHow ──────────
        private static readonly Color CLR_BG           = Color.FromArgb(22, 22, 30);    // deepest background
        private static readonly Color CLR_CARD         = Color.FromArgb(32, 34, 44);    // card surface
        private static readonly Color CLR_CARD_HOVER   = Color.FromArgb(38, 40, 52);    // card hover
        private static readonly Color CLR_CARD_BORDER  = Color.FromArgb(52, 56, 72);    // card border
        private static readonly Color CLR_TEXT          = Color.FromArgb(225, 228, 235); // primary text
        private static readonly Color CLR_TEXT_DIM      = Color.FromArgb(130, 135, 155); // secondary text
        private static readonly Color CLR_ACCENT       = Color.FromArgb(88, 138, 255);  // blue accent (step numbers)
        private static readonly Color CLR_ACCENT2      = Color.FromArgb(60, 190, 120);  // green accent (generate)
        private static readonly Color CLR_DANGER       = Color.FromArgb(220, 60, 60);   // red (delete)
        private static readonly Color CLR_TOOLBAR      = Color.FromArgb(26, 28, 36);    // toolbar bg
        private static readonly Color CLR_INPUT        = Color.FromArgb(40, 42, 54);    // input bg

        private static readonly Font FNT_STEP_NUM     = new Font("Segoe UI", 14f, FontStyle.Bold);
        private static readonly Font FNT_TITLE        = new Font("Segoe UI Semibold", 11f);
        private static readonly Font FNT_BODY         = new Font("Segoe UI", 9.5f);
        private static readonly Font FNT_SMALL        = new Font("Segoe UI", 8.5f);
        private static readonly Font FNT_BTN          = new Font("Segoe UI", 8.5f);

        // ─── Controls ─────────────────────────────────────────────────────
        private FlowLayoutPanel     pnlToolbar;
        private Panel               pnlCards;         // scrollable card container (legacy mode)
        private SplitContainer      splitMain;        // 2-pane UI for View-Centric mode
        private TreeView            treePages;        // Left pane: Menu/View hierarchy
        private Panel               pnlViewDetail;    // Right pane: view editor
        private Label               lblStatus;
        private ProgressBar         progressBar;
        private ComboBox            cmbProcess;
        private ComboBox            cmbMode;

        // ─── State ────────────────────────────────────────────────────────
        private ManualDocument          _document;
        private List<WindowSnapshot>    _snapshots;
        private SourceAnalyzer          _sourceAnalyzer;
        private ManualGenerator         _generator;
        private CancellationTokenSource _cts;
        private string                  _tempDir;

        // View-Centric (XML Menu -> Prism View) Mode State
        private ManualProject           _project;
        private PrismViewResolver       _viewResolver;
        private ViewManualGenerator     _viewGenerator;
        private ManualPage              _selectedPage;

        private List<(int Pid, string Name, string Title, IntPtr Hwnd)> _processes
            = new List<(int, string, string, IntPtr)>();

        // ─── Constructor ──────────────────────────────────────────────────

        public ManualEditorPanel()
        {
            _generator = new ManualGenerator(maxParallel: 3);
            _generator.ProgressChanged += (done, total) =>
            {
                if (InvokeRequired) BeginInvoke((Action)(() => UpdateProgress(done, total)));
                else UpdateProgress(done, total);
            };

            _viewGenerator = new ViewManualGenerator();

            _tempDir = Path.Combine(Path.GetTempPath(),
                "AIAssistant_Manual_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_tempDir);

            DoubleBuffered = true;
            BuildUI();
            RefreshProcessList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            BackColor = CLR_BG;
            Padding = Padding.Empty;

            // ── Toolbar ──
            pnlToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 44,
                BackColor = CLR_TOOLBAR,
                Padding = new Padding(12, 8, 12, 8),
                WrapContents = false,
                AutoScroll = true
            };

            // Mode
            AddLabel(pnlToolbar, "Mode", 4);
            cmbMode = CreateCombo(185);
            cmbMode.Items.AddRange(new object[] { "📋 UI XML (View 단위)", "📸 Current Screen", "🌐 Full App (Auto-Crawl)" });
            cmbMode.SelectedIndex = 0;
            cmbMode.SelectedIndexChanged += (s, e) => OnModeChanged();
            pnlToolbar.Controls.Add(cmbMode);

            AddSpacer(pnlToolbar, 8);

            // Target process
            AddLabel(pnlToolbar, "Target", 4);
            cmbProcess = CreateCombo(200);
            pnlToolbar.Controls.Add(cmbProcess);

            var btnRefresh = CreateIconBtn("🔄", "Refresh Processes", CLR_CARD);
            btnRefresh.Click += (s, e) => RefreshProcessList();
            pnlToolbar.Controls.Add(btnRefresh);

            AddSpacer(pnlToolbar, 10);

            // XML & Project Action buttons
            var btnOpenXml = CreateAccentBtn("📂 Open XML", Color.FromArgb(70, 110, 150));
            btnOpenXml.Tag = "btnOpenXml";
            btnOpenXml.Click += BtnOpenXml_Click;
            pnlToolbar.Controls.Add(btnOpenXml);

            var btnSaveProj = CreateIconBtn("💾", "Save Project (.manual.json)", CLR_CARD);
            btnSaveProj.Tag = "btnSaveProj";
            btnSaveProj.Click += BtnSaveProj_Click;
            pnlToolbar.Controls.Add(btnSaveProj);

            var btnLoadProj = CreateIconBtn("📂", "Open Project (.manual.json)", CLR_CARD);
            btnLoadProj.Tag = "btnLoadProj";
            btnLoadProj.Click += BtnLoadProj_Click;
            pnlToolbar.Controls.Add(btnLoadProj);

            AddSpacer(pnlToolbar, 10);

            // Action buttons
            var btnCapture  = CreateAccentBtn("📸 Capture",  CLR_ACCENT);
            var btnGenerate = CreateAccentBtn("✨ Generate", CLR_ACCENT2);
            var btnExport   = CreateAccentBtn("📄 Export DOCX", Color.FromArgb(60, 100, 160));
            var btnCancel   = CreateAccentBtn("⏹ Cancel",    CLR_DANGER);

            btnCapture.Click  += BtnCapture_Click;
            btnGenerate.Click += BtnGenerate_Click;
            btnExport.Click   += BtnExport_Click;
            btnCancel.Click   += BtnCancel_Click;

            btnGenerate.Tag = "btnGenerate"; btnGenerate.Enabled = false;
            btnExport.Tag   = "btnExport";   btnExport.Enabled   = false;
            btnCancel.Tag   = "btnCancel";   btnCancel.Enabled   = false;

            pnlToolbar.Controls.AddRange(new Control[] { btnCapture, btnGenerate, btnExport, btnCancel });

            // ── Progress ──
            progressBar = new ProgressBar
            {
                Dock = DockStyle.Top, Height = 3,
                BackColor = CLR_BG, ForeColor = CLR_ACCENT,
                Style = ProgressBarStyle.Continuous, Visible = false
            };

            // ── Status bar ──
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom, Height = 24,
                Text = "WPF 앱을 실행 → Target 선택 → 📸 Capture",
                BackColor = Color.FromArgb(18, 18, 24), ForeColor = CLR_TEXT_DIM,
                Font = FNT_SMALL, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            // ── Card container (scrollable - legacy mode) ──
            pnlCards = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = CLR_BG,
                Padding = new Padding(0, 8, 0, 8),
                Visible = false
            };

            // ── Split container (View-Centric mode) ──
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 280,
                SplitterWidth = 5,
                BackColor = CLR_CARD_BORDER
            };

            // Left: TreeView
            treePages = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = CLR_TOOLBAR,
                ForeColor = CLR_TEXT,
                BorderStyle = BorderStyle.None,
                Font = FNT_BODY,
                FullRowSelect = true,
                ShowLines = true,
                HideSelection = false
            };
            treePages.AfterSelect += (s, e) =>
            {
                if (e.Node?.Tag is ManualPage page)
                {
                    _selectedPage = page;
                    RenderViewDetail(page);
                }
            };
            splitMain.Panel1.Controls.Add(treePages);

            // Right: Detail panel
            pnlViewDetail = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = CLR_BG,
                Padding = new Padding(12)
            };
            splitMain.Panel2.Controls.Add(pnlViewDetail);

            // Welcome message in pnlViewDetail & pnlCards
            ShowWelcomeCard();

            // Assemble (bottom-up order in Controls.Add)
            Controls.Add(splitMain);
            Controls.Add(pnlCards);
            Controls.Add(progressBar);
            Controls.Add(pnlToolbar);
            Controls.Add(lblStatus);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  STEP CARDS — the core Trupeer/StepHow-style UI elements
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Renders all blocks as step cards in the scrollable panel.</summary>
        private void RenderCards()
        {
            pnlCards.SuspendLayout();
            pnlCards.Controls.Clear();

            if (_document == null || _document.Blocks.Count == 0)
            {
                ShowWelcomeCard();
                pnlCards.ResumeLayout();
                return;
            }

            int stepNum = 0;
            int yPos = 12;
            int cardWidth = Math.Max(400, pnlCards.ClientSize.Width - 48);

            foreach (var block in _document.Blocks.Where(b => b.IsIncluded).OrderBy(b => b.Order))
            {
                Panel card;
                if (block.Type == ManualBlockType.Heading)
                    card = CreateHeadingCard(block, cardWidth);
                else if (block.Type == ManualBlockType.Screenshot)
                    card = CreateScreenshotCard(block, cardWidth);
                else if (block.Type == ManualBlockType.ControlItem)
                {
                    stepNum++;
                    card = CreateControlCard(block, stepNum, cardWidth);
                }
                else
                    card = CreateGenericCard(block, cardWidth);

                card.Location = new Point(20, yPos);
                card.Width = cardWidth;
                pnlCards.Controls.Add(card);
                yPos += card.Height + 10;
            }

            // + Add Step card
            var addBtn = CreateAddStepCard(cardWidth);
            addBtn.Location = new Point(20, yPos);
            pnlCards.Controls.Add(addBtn);

            pnlCards.ResumeLayout();
        }

        // ── Heading Card ──────────────────────────────────────────────────

        private Panel CreateHeadingCard(ManualBlock block, int w)
        {
            var card = new Panel
            {
                Width = w, Height = 52, BackColor = CLR_CARD,
                Tag = block, Cursor = Cursors.Default
            };
            card.Paint += (s, e) => PaintCardBorder(e.Graphics, card, CLR_ACCENT, 2);

            // Accent left bar
            var accent = new Panel
            {
                Location = new Point(0, 0), Width = 4, Height = card.Height,
                BackColor = CLR_ACCENT
            };
            card.Controls.Add(accent);

            // Title (editable)
            var txtTitle = new TextBox
            {
                Text = block.Title, Font = FNT_TITLE, ForeColor = CLR_TEXT,
                BackColor = CLR_CARD, BorderStyle = BorderStyle.None,
                Location = new Point(16, 14), Width = w - 100
            };
            txtTitle.TextChanged += (s, e) => block.Title = txtTitle.Text;
            card.Controls.Add(txtTitle);

            // Delete button
            var btnDel = CreateSmallBtn("✕", CLR_DANGER);
            btnDel.Location = new Point(w - 36, 14);
            btnDel.Click += (s, e) => RemoveBlock(block);
            card.Controls.Add(btnDel);

            return card;
        }

        // ── Screenshot Card (with dynamic callout controls) ──────────────

        private Panel CreateScreenshotCard(ManualBlock block, int w)
        {
            // Find associated snapshot
            var snapshot = _snapshots?.FirstOrDefault(sn =>
                sn.WindowTitle == block.WindowName &&
                (sn.TabContext ?? "") == (block.TabContext ?? ""));

            int imgHeight = 300;
            int calloutBarHeight = (block.Callouts?.Count > 0) ? 32 + Math.Min(block.Callouts.Count, 6) * 24 : 60;
            int totalHeight = imgHeight + calloutBarHeight + 50;

            var card = new Panel
            {
                Width = w, Height = totalHeight, BackColor = CLR_CARD,
                Tag = block, Cursor = Cursors.Default
            };
            card.Paint += (s, e) => PaintCardBorder(e.Graphics, card, CLR_CARD_BORDER, 1);

            // ── Top bar: title + callout toggle ──
            var txtTitle = new TextBox
            {
                Text = block.Title, Font = FNT_BODY, ForeColor = CLR_TEXT_DIM,
                BackColor = CLR_CARD, BorderStyle = BorderStyle.None,
                Location = new Point(12, 8), Width = w - 180
            };
            txtTitle.TextChanged += (s, e) => block.Title = txtTitle.Text;
            card.Controls.Add(txtTitle);

            var chkCallouts = new CheckBox
            {
                Text = "Show Callouts", Checked = block.ShowCallouts,
                ForeColor = CLR_TEXT_DIM, Font = FNT_SMALL,
                AutoSize = true, Location = new Point(w - 160, 8),
                BackColor = CLR_CARD, FlatStyle = FlatStyle.Flat
            };
            chkCallouts.CheckedChanged += (s, e) =>
            {
                block.ShowCallouts = chkCallouts.Checked;
                RefreshScreenshotImage(card, block, snapshot);
            };
            card.Controls.Add(chkCallouts);

            // ── Screenshot image ──
            var pic = new PictureBox
            {
                Location = new Point(12, 32), Width = w - 24, Height = imgHeight,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 26),
                BorderStyle = BorderStyle.None, Tag = "pic"
            };
            // Render annotated bitmap
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.ScreenshotPath))
            {
                var bmp = ScreenCaptureEngine.RenderAnnotatedBitmap(
                    snapshot.ScreenshotPath, block.ShowCallouts ? block.Callouts : null,
                    snapshot.WindowRect, block.ShowCallouts);
                pic.Image = bmp;
            }
            card.Controls.Add(pic);

            // ── Callout list with insert/delete ──
            int cy = 32 + imgHeight + 8;

            var lblCallouts = new Label
            {
                Text = $"Callouts ({block.Callouts?.Count ?? 0})", Font = FNT_SMALL,
                ForeColor = CLR_ACCENT, AutoSize = true,
                Location = new Point(12, cy), BackColor = CLR_CARD
            };
            card.Controls.Add(lblCallouts);

            var btnAddCallout = CreateSmallBtn("+ Add", CLR_ACCENT);
            btnAddCallout.Location = new Point(160, cy - 2);
            btnAddCallout.Width = 55;
            btnAddCallout.Click += (s, e) =>
            {
                if (snapshot == null) return;
                // Add next unassigned important element
                var usedIds = new HashSet<string>(block.Callouts.Select(c => c.ElementId));
                var nextEl = snapshot.Elements
                    .Where(el => el.ImportanceScore >= 3 && !usedIds.Contains(el.UniqueId) && !el.IsOffscreen)
                    .OrderByDescending(el => el.ImportanceScore)
                    .FirstOrDefault();

                if (nextEl != null)
                {
                    int nextNum = block.Callouts.Count > 0 ? block.Callouts.Max(c => c.Number) + 1 : 1;
                    block.Callouts.Add(new CalloutItem
                    {
                        Number    = nextNum,
                        ElementId = nextEl.UniqueId,
                        Label     = string.IsNullOrEmpty(nextEl.Name) ? nextEl.ControlType : nextEl.Name,
                        Rect      = nextEl.BoundingRectangle
                    });
                    nextEl.CalloutNumber = nextNum;
                    RenderCards();
                }
                else
                {
                    lblStatus.Text = "ℹ️ 추가할 수 있는 UI 요소가 없습니다.";
                }
            };
            card.Controls.Add(btnAddCallout);

            var btnClearCallouts = CreateSmallBtn("Clear All", Color.FromArgb(100, 60, 60));
            btnClearCallouts.Location = new Point(220, cy - 2);
            btnClearCallouts.Width = 65;
            btnClearCallouts.Click += (s, e) =>
            {
                block.Callouts.Clear();
                RenderCards();
            };
            card.Controls.Add(btnClearCallouts);

            // Individual callout items
            if (block.Callouts != null)
            {
                int ciy = cy + 24;
                foreach (var callout in block.Callouts.ToList())
                {
                    var lbl = new Label
                    {
                        Text = $"  {GetCircle(callout.Number)}  {callout.Label}",
                        Font = FNT_SMALL, ForeColor = CLR_TEXT, AutoSize = false,
                        Location = new Point(16, ciy), Width = w - 80, Height = 20,
                        BackColor = CLR_CARD
                    };
                    card.Controls.Add(lbl);

                    var btnRemove = CreateSmallBtn("✕", CLR_DANGER);
                    btnRemove.Location = new Point(w - 52, ciy);
                    btnRemove.Width = 24; btnRemove.Height = 20;
                    var capturedCallout = callout;
                    btnRemove.Click += (s, e) =>
                    {
                        block.Callouts.Remove(capturedCallout);
                        RenumberCallouts(block.Callouts);
                        RenderCards();
                    };
                    card.Controls.Add(btnRemove);
                    ciy += 24;
                }
            }

            return card;
        }

        // ── Control Item Card (the core "step" card) ─────────────────────

        private Panel CreateControlCard(ManualBlock block, int stepNum, int w)
        {
            int h = string.IsNullOrEmpty(block.Content) ? 100 : 
                    Math.Min(220, 80 + (block.Content.Split('\n').Length * 18));
            if (block.Steps?.Count > 0) h += block.Steps.Count * 18 + 20;

            var card = new Panel
            {
                Width = w, Height = h, BackColor = CLR_CARD,
                Tag = block, Cursor = Cursors.Default
            };
            card.Paint += (s, e) => PaintCardBorder(e.Graphics, card, CLR_CARD_BORDER, 1);
            card.MouseEnter += (s, e) => card.BackColor = CLR_CARD_HOVER;
            card.MouseLeave += (s, e) => card.BackColor = CLR_CARD;

            // ── Step number circle ──
            var pnlStep = new Panel
            {
                Location = new Point(12, 12), Width = 36, Height = 36,
                BackColor = Color.Transparent
            };
            pnlStep.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(CLR_ACCENT))
                    e.Graphics.FillEllipse(brush, 0, 0, 34, 34);
                using (var font = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString(stepNum.ToString(), font, brush, new RectangleF(0, 0, 34, 34), sf);
                }
            };
            card.Controls.Add(pnlStep);

            // ── Title (editable) ──
            var txtTitle = new TextBox
            {
                Text = block.Title, Font = FNT_TITLE, ForeColor = CLR_TEXT,
                BackColor = CLR_CARD, BorderStyle = BorderStyle.None,
                Location = new Point(56, 12), Width = w - 200
            };
            txtTitle.TextChanged += (s, e) => block.Title = txtTitle.Text;
            txtTitle.Enter += (s, e) => txtTitle.BackColor = CLR_INPUT;
            txtTitle.Leave += (s, e) => { txtTitle.BackColor = CLR_CARD; block.Title = txtTitle.Text; };
            card.Controls.Add(txtTitle);

            // Callout badge
            if (block.CalloutNumber.HasValue)
            {
                var badge = new Label
                {
                    Text = GetCircle(block.CalloutNumber.Value),
                    Font = FNT_BODY, ForeColor = CLR_ACCENT, AutoSize = true,
                    Location = new Point(56, 36), BackColor = CLR_CARD
                };
                card.Controls.Add(badge);
            }

            // ── Action buttons (top-right) ──
            int bx = w - 140;
            var btnRegen = CreateSmallBtn("🔄", CLR_ACCENT);
            btnRegen.Location = new Point(bx, 12);
            btnRegen.Click += async (s, e) => await RegenerateBlockAsync(block);
            card.Controls.Add(btnRegen);

            var btnUp = CreateSmallBtn("⬆", CLR_CARD_BORDER);
            btnUp.Location = new Point(bx + 30, 12);
            btnUp.Click += (s, e) => MoveBlock(block, -1);
            card.Controls.Add(btnUp);

            var btnDown = CreateSmallBtn("⬇", CLR_CARD_BORDER);
            btnDown.Location = new Point(bx + 56, 12);
            btnDown.Click += (s, e) => MoveBlock(block, +1);
            card.Controls.Add(btnDown);

            var btnDel = CreateSmallBtn("✕", CLR_DANGER);
            btnDel.Location = new Point(bx + 82, 12);
            btnDel.Click += (s, e) => RemoveBlock(block);
            card.Controls.Add(btnDel);

            // ── Description (editable multi-line) ──
            int textY = block.CalloutNumber.HasValue ? 56 : 42;
            var txtDesc = new TextBox
            {
                Text = block.Content ?? "", Font = FNT_BODY, ForeColor = CLR_TEXT_DIM,
                BackColor = CLR_CARD, BorderStyle = BorderStyle.None,
                Location = new Point(56, textY), Width = w - 80,
                Height = h - textY - 8 - (block.Steps?.Count > 0 ? block.Steps.Count * 18 + 10 : 0),
                Multiline = true, ScrollBars = ScrollBars.None, WordWrap = true
            };
            txtDesc.TextChanged += (s, e) => block.Content = txtDesc.Text;
            txtDesc.Enter += (s, e) => txtDesc.BackColor = CLR_INPUT;
            txtDesc.Leave += (s, e) => txtDesc.BackColor = CLR_CARD;
            card.Controls.Add(txtDesc);

            // ── Steps (numbered list) ──
            if (block.Steps?.Count > 0)
            {
                int sy = h - block.Steps.Count * 18 - 8;
                for (int i = 0; i < block.Steps.Count; i++)
                {
                    var lbl = new Label
                    {
                        Text = $"  {i + 1}. {block.Steps[i]}", Font = FNT_SMALL,
                        ForeColor = CLR_TEXT, AutoSize = false,
                        Location = new Point(56, sy + i * 18),
                        Width = w - 80, Height = 18, BackColor = CLR_CARD
                    };
                    card.Controls.Add(lbl);
                }
            }

            // Confidence indicator
            if (block.Confidence > 0 && block.Confidence < 0.6)
            {
                var warn = new Label
                {
                    Text = $"⚠️ 신뢰도 낮음 ({block.Confidence:P0})", Font = FNT_SMALL,
                    ForeColor = Color.FromArgb(220, 160, 50), AutoSize = true,
                    Location = new Point(56, h - 20), BackColor = CLR_CARD
                };
                card.Controls.Add(warn);
            }

            return card;
        }

        // ── Generic Card ─────────────────────────────────────────────────

        private Panel CreateGenericCard(ManualBlock block, int w)
        {
            var card = new Panel
            {
                Width = w, Height = 60, BackColor = CLR_CARD, Tag = block
            };
            card.Paint += (s, e) => PaintCardBorder(e.Graphics, card, CLR_CARD_BORDER, 1);

            var lbl = new Label
            {
                Text = $"  {block.Title}", Font = FNT_BODY,
                ForeColor = CLR_TEXT_DIM, AutoSize = true,
                Location = new Point(12, 18), BackColor = CLR_CARD
            };
            card.Controls.Add(lbl);
            return card;
        }

        // ── Add Step Card ────────────────────────────────────────────────

        private Panel CreateAddStepCard(int w)
        {
            var card = new Panel
            {
                Width = w, Height = 48, BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(CLR_CARD_BORDER, 1.5f) { DashStyle = DashStyle.Dash })
                    e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3);

                using (var brush = new SolidBrush(CLR_TEXT_DIM))
                using (var font = new Font("Segoe UI", 10f))
                {
                    var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString("＋ Add Step", font, brush,
                        new RectangleF(0, 0, card.Width, card.Height), sf);
                }
            };
            card.Click += (s, e) => AddManualStep();
            return card;
        }

        // ── Welcome Card ─────────────────────────────────────────────────

        private void ShowWelcomeCard()
        {
            pnlCards.Controls.Clear();
            int w = Math.Max(400, pnlCards.ClientSize.Width - 48);

            var card = new Panel
            {
                Width = w, Height = 200, BackColor = CLR_CARD,
                Location = new Point(20, 40)
            };
            card.Paint += (s, e) => PaintCardBorder(e.Graphics, card, CLR_ACCENT, 1);

            string[] lines =
            {
                "📖  WPF Auto Manual Generator",
                "",
                "【📋 UI XML (View 단위) 모드】",
                "1.  📂 Open XML 버튼을 눌러 대상 프로그램의 UI 정의 XML을 엽니다.",
                "2.  XML의 <Menu> 계층(DisplayName)과 Prism View/ViewModel 소스가 자동 매핑됩니다.",
                "3.  좌측 메뉴 트리에서 원하는 페이지를 선택하거나, 📸 Capture로 등록된 View를 자동 방문·캡처합니다.",
                "4.  ✨ Generate로 View XAML 및 ViewModel 코드를 바탕으로 LLM 매뉴얼을 작성합니다.",
                "5.  우측 편집 패널에서 개요, 컨트롤 설명표, 절차, 주의사항을 수정한 후 📄 Export DOCX를 클릭하세요.",
                "",
                "【📸 직접 화면 캡처 모드】",
                "Mode를 '📸 Current Screen' 또는 '🌐 Full App'으로 변경하여 실행 중인 창을 직접 캡처할 수도 있습니다."
            };
            int y = 20;
            foreach (var line in lines)
            {
                bool isHead = line.StartsWith("📖") || line.StartsWith("【");
                var lbl = new Label
                {
                    Text = line,
                    Font = line.StartsWith("📖") ? FNT_TITLE : (isHead ? FNT_TITLE : FNT_BODY),
                    ForeColor = line.StartsWith("📖") ? CLR_ACCENT : (isHead ? CLR_ACCENT2 : CLR_TEXT),
                    AutoSize = true, Location = new Point(24, y), BackColor = CLR_CARD
                };
                card.Controls.Add(lbl);
                y += line.StartsWith("📖") ? 28 : (isHead ? 24 : 18);
            }
            card.Height = y + 20;
            pnlCards.Controls.Add(card);

            if (pnlViewDetail != null)
            {
                pnlViewDetail.Controls.Clear();
                var cardDetail = new Panel
                {
                    Width = Math.Max(400, pnlViewDetail.ClientSize.Width - 48),
                    Height = card.Height, BackColor = CLR_CARD, Location = new Point(20, 20)
                };
                cardDetail.Paint += (s, e) => PaintCardBorder(e.Graphics, cardDetail, CLR_ACCENT, 1);
                int dy = 20;
                foreach (var line in lines)
                {
                    bool isHead = line.StartsWith("📖") || line.StartsWith("【");
                    var lbl = new Label
                    {
                        Text = line,
                        Font = line.StartsWith("📖") ? FNT_TITLE : (isHead ? FNT_TITLE : FNT_BODY),
                        ForeColor = line.StartsWith("📖") ? CLR_ACCENT : (isHead ? CLR_ACCENT2 : CLR_TEXT),
                        AutoSize = true, Location = new Point(24, dy), BackColor = CLR_CARD
                    };
                    cardDetail.Controls.Add(lbl);
                    dy += line.StartsWith("📖") ? 28 : (isHead ? 24 : 18);
                }
                pnlViewDetail.Controls.Add(cardDetail);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ACTIONS
        // ═══════════════════════════════════════════════════════════════════

        private void OnModeChanged()
        {
            bool isXmlMode = cmbMode.SelectedIndex == 0;
            splitMain.Visible = isXmlMode;
            pnlCards.Visible = !isXmlMode;
            FindBtn("btnOpenXml").Visible = isXmlMode;
            FindBtn("btnSaveProj").Visible = isXmlMode;
            FindBtn("btnLoadProj").Visible = isXmlMode;

            if (isXmlMode)
            {
                lblStatus.Text = _project == null
                    ? "📂 Open XML로 메뉴 XML 파일을 열거나 💾 프로젝트를 불러오세요."
                    : $"📋 프로젝트: {_project.AllPages().Count()}개 페이지";
                if (_project == null) ShowWelcomeCard();
            }
            else
            {
                lblStatus.Text = "WPF 앱을 실행 → Target 선택 → 📸 Capture";
                if (_document == null) ShowWelcomeCard();
            }
        }

        // ── XML & Project actions ──

        private void BtnOpenXml_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "WPF UI 정의 XML 파일 선택",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var roots = UiMenuXmlParser.Parse(dlg.FileName);
                    if (roots.Count == 0)
                    {
                        MessageBox.Show("XML에서 유효한 <Menu> 요소를 찾지 못했습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string solDir = GetSolutionDirectory() ?? Path.GetDirectoryName(dlg.FileName);
                    _project = new ManualProject
                    {
                        AppName    = Path.GetFileNameWithoutExtension(dlg.FileName),
                        Title      = $"{Path.GetFileNameWithoutExtension(dlg.FileName)} 사용자 매뉴얼",
                        UiXmlPath  = dlg.FileName,
                        SourceRoot = solDir,
                        Roots      = roots
                    };

                    _viewResolver = new PrismViewResolver(solDir);
                    SetBusy(true, "🔍 Prism View/ViewModel 소스 매핑 중...");

                    Task.Run(() =>
                    {
                        _viewResolver.BuildIndex();
                        foreach (var page in _project.AllPages())
                            _viewResolver.ApplyTo(page);
                    }).ContinueWith(t =>
                    {
                        SetBusy(false, "");
                        PopulatePagesTree();
                        lblStatus.Text = $"✅ XML 로드 완료: {_project.AllPages().Count()}개 메뉴 (Prism View 매핑 완료)";
                        FindBtn("btnGenerate").Enabled = true;
                        FindBtn("btnExport").Enabled   = true;
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"XML 파일 파싱 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnSaveProj_Click(object sender, EventArgs e)
        {
            if (_project == null) { MessageBox.Show("저장할 프로젝트가 없습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            string path = _project.ProjectPath;
            if (string.IsNullOrEmpty(path))
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "Manual Project 저장",
                    Filter = ManualProjectStore.FileFilter,
                    FileName = $"{_project.AppName}_Manual.manual.json"
                })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            }

            try
            {
                ManualProjectStore.Save(_project, path);
                lblStatus.Text = $"💾 프로젝트 저장 완료: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"프로젝트 저장 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnLoadProj_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Manual Project 열기",
                Filter = ManualProjectStore.FileFilter
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    _project = ManualProjectStore.Load(dlg.FileName);
                    string solDir = _project.SourceRoot ?? GetSolutionDirectory();
                    _viewResolver = new PrismViewResolver(solDir);
                    _viewResolver.BuildIndex();

                    PopulatePagesTree();
                    lblStatus.Text = $"📂 프로젝트 로드 완료: {_project.AllPages().Count()}개 페이지";
                    FindBtn("btnGenerate").Enabled = true;
                    FindBtn("btnExport").Enabled   = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"프로젝트 열기 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ── TreeView Population & View Detail Panel ──

        private void PopulatePagesTree()
        {
            treePages.BeginUpdate();
            treePages.Nodes.Clear();

            if (_project != null)
            {
                foreach (var root in _project.Roots)
                    treePages.Nodes.Add(BuildTreeNode(root));
                treePages.ExpandAll();
                if (treePages.Nodes.Count > 0)
                    treePages.SelectedNode = treePages.Nodes[0];
            }

            treePages.EndUpdate();
        }

        private TreeNode BuildTreeNode(ManualPage page)
        {
            string icon = page.HasScreenshot && page.HasContent ? "✅ " :
                          page.HasScreenshot ? "📸 " :
                          page.HasContent ? "📝 " :
                          page.HasView ? "📄 " : "📁 ";
            string text = icon + page.DisplayName + (string.IsNullOrEmpty(page.ViewName) ? "" : $" ({page.ViewName})");
            var node = new TreeNode(text) { Tag = page };
            foreach (var ch in page.Children)
                node.Nodes.Add(BuildTreeNode(ch));
            return node;
        }

        private void RenderViewDetail(ManualPage page)
        {
            pnlViewDetail.SuspendLayout();
            pnlViewDetail.Controls.Clear();

            if (page == null)
            {
                pnlViewDetail.ResumeLayout();
                return;
            }

            int w = Math.Max(480, pnlViewDetail.ClientSize.Width - 32);
            int y = 12;

            // Header Box
            var pnlHead = new Panel
            {
                Location = new Point(12, y), Width = w, Height = 95,
                BackColor = CLR_CARD
            };
            pnlHead.Paint += (s, e) => PaintCardBorder(e.Graphics, pnlHead, CLR_ACCENT, 1);

            var lblTitle = new Label
            {
                Text = $"📄 {page.DisplayName}  ›  {page.PathText}",
                Font = FNT_TITLE, ForeColor = CLR_ACCENT, AutoSize = true,
                Location = new Point(12, 10), BackColor = CLR_CARD
            };
            pnlHead.Controls.Add(lblTitle);

            var lblView = new Label
            {
                Text = $"View: {page.ViewName ?? "(없음)"}  |  ViewModel: {page.ViewModelTypeName ?? "(미지정)"}",
                Font = FNT_SMALL, ForeColor = CLR_TEXT, AutoSize = true,
                Location = new Point(14, 38), BackColor = CLR_CARD
            };
            pnlHead.Controls.Add(lblView);

            var lblSrc = new Label
            {
                Text = $"XAML: {Trunc(page.ViewXamlPath ?? "미발견", 55)}  /  VM: {Trunc(page.ViewModelPath ?? "미발견", 55)}",
                Font = FNT_SMALL, ForeColor = CLR_TEXT_DIM, AutoSize = true,
                Location = new Point(14, 58), BackColor = CLR_CARD
            };
            pnlHead.Controls.Add(lblSrc);

            // Action Buttons for this Page
            var btnNavCap = CreateAccentBtn("🎯 Navigate & 📸 Capture", CLR_ACCENT);
            btnNavCap.Location = new Point(w - 380, 10);
            btnNavCap.Click += async (s, e) => await CapturePageAsync(page);
            pnlHead.Controls.Add(btnNavCap);

            var btnGenThis = CreateAccentBtn("✨ Generate (LLM)", CLR_ACCENT2);
            btnGenThis.Location = new Point(w - 180, 10);
            btnGenThis.Click += async (s, e) => await GeneratePageAsync(page);
            pnlHead.Controls.Add(btnGenThis);

            pnlViewDetail.Controls.Add(pnlHead);
            y += pnlHead.Height + 14;

            // Screenshot Preview
            int imgH = 260;
            var pnlImg = new Panel
            {
                Location = new Point(12, y), Width = w, Height = imgH + 40,
                BackColor = CLR_CARD
            };
            pnlImg.Paint += (s, e) => PaintCardBorder(e.Graphics, pnlImg, CLR_CARD_BORDER, 1);

            var lblImgHead = new Label
            {
                Text = $"화면 캡처 (Callouts: {page.Callouts.Count}개)",
                Font = FNT_SMALL, ForeColor = CLR_TEXT_DIM, AutoSize = true,
                Location = new Point(12, 8), BackColor = CLR_CARD
            };
            pnlImg.Controls.Add(lblImgHead);

            var chkShowCallouts = new CheckBox
            {
                Text = "Show Callouts", Checked = page.ShowCallouts,
                ForeColor = CLR_TEXT_DIM, Font = FNT_SMALL,
                AutoSize = true, Location = new Point(w - 140, 8),
                BackColor = CLR_CARD, FlatStyle = FlatStyle.Flat
            };

            var pic = new PictureBox
            {
                Location = new Point(12, 32), Width = w - 24, Height = imgH,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 26),
                BorderStyle = BorderStyle.None
            };

            Action refreshPic = () =>
            {
                if (page.HasScreenshot)
                {
                    var old = pic.Image;
                    pic.Image = ScreenCaptureEngine.RenderAnnotatedBitmap(
                        page.ScreenshotPath, page.ShowCallouts ? page.CalloutItems() : null,
                        page.WindowRect, page.ShowCallouts);
                    old?.Dispose();
                }
                else pic.Image = null;
            };

            chkShowCallouts.CheckedChanged += (s, e) =>
            {
                page.ShowCallouts = chkShowCallouts.Checked;
                refreshPic();
            };
            pnlImg.Controls.Add(chkShowCallouts);
            pnlImg.Controls.Add(pic);
            refreshPic();

            pnlViewDetail.Controls.Add(pnlImg);
            y += pnlImg.Height + 14;

            // Summary (개요)
            var lblSum = new Label { Text = "📝 화면 개요 (Summary)", Font = FNT_BODY, ForeColor = CLR_ACCENT, Location = new Point(12, y), AutoSize = true };
            pnlViewDetail.Controls.Add(lblSum);
            y += 24;

            var txtSum = new TextBox
            {
                Text = page.Summary ?? "", Location = new Point(12, y), Width = w, Height = 75,
                Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                BorderStyle = BorderStyle.FixedSingle, Font = FNT_BODY
            };
            txtSum.TextChanged += (s, e) => page.Summary = txtSum.Text;
            pnlViewDetail.Controls.Add(txtSum);
            y += txtSum.Height + 14;

            // Controls Grid (컨트롤 설명 목록)
            var lblCtrl = new Label { Text = "🎛 컨트롤 설명 (Controls)", Font = FNT_BODY, ForeColor = CLR_ACCENT, Location = new Point(12, y), AutoSize = true };
            pnlViewDetail.Controls.Add(lblCtrl);
            y += 24;

            var grid = new DataGridView
            {
                Location = new Point(12, y), Width = w, Height = 170,
                BackgroundColor = CLR_CARD, ForeColor = CLR_TEXT,
                GridColor = CLR_CARD_BORDER, BorderStyle = BorderStyle.None,
                RowHeadersVisible = false, AllowUserToAddRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = CLR_TOOLBAR;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = CLR_TEXT;
            grid.DefaultCellStyle.BackColor = CLR_INPUT;
            grid.DefaultCellStyle.ForeColor = CLR_TEXT;
            grid.DefaultCellStyle.SelectionBackColor = CLR_CARD_HOVER;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;

            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "번호", DataPropertyName = "Callout", Width = 50, FillWeight = 15 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "컨트롤 이름", DataPropertyName = "Name", Width = 150, FillWeight = 35 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "기능 / 사용 조건", DataPropertyName = "Description", FillWeight = 50 });

            var bindingList = new System.ComponentModel.BindingList<ControlDesc>(page.Controls);
            grid.DataSource = bindingList;
            pnlViewDetail.Controls.Add(grid);
            y += grid.Height + 14;

            // Procedures (사용 절차)
            var lblProc = new Label { Text = "📋 작업/사용 절차 (Procedures)", Font = FNT_BODY, ForeColor = CLR_ACCENT, Location = new Point(12, y), AutoSize = true };
            pnlViewDetail.Controls.Add(lblProc);
            y += 24;

            var txtProc = new TextBox
            {
                Text = page.Procedures ?? "", Location = new Point(12, y), Width = w, Height = 75,
                Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                BorderStyle = BorderStyle.FixedSingle, Font = FNT_BODY
            };
            txtProc.TextChanged += (s, e) => page.Procedures = txtProc.Text;
            pnlViewDetail.Controls.Add(txtProc);
            y += txtProc.Height + 14;

            // Notes (주의사항)
            var lblNotes = new Label { Text = "⚠️ 주의사항 (Notes)", Font = FNT_BODY, ForeColor = Color.FromArgb(220, 160, 50), Location = new Point(12, y), AutoSize = true };
            pnlViewDetail.Controls.Add(lblNotes);
            y += 24;

            var txtNotes = new TextBox
            {
                Text = page.Notes ?? "", Location = new Point(12, y), Width = w, Height = 60,
                Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                BorderStyle = BorderStyle.FixedSingle, Font = FNT_BODY
            };
            txtNotes.TextChanged += (s, e) => page.Notes = txtNotes.Text;
            pnlViewDetail.Controls.Add(txtNotes);

            pnlViewDetail.ResumeLayout();
        }

        private async Task CapturePageAsync(ManualPage page)
        {
            if (cmbProcess.SelectedIndex < 0 || _processes.Count == 0)
            {
                MessageBox.Show("먼저 대상 WPF Process를 Target 드롭다운에서 선택하세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var proc = _processes[cmbProcess.SelectedIndex];
            SetBusy(true, $"🎯 '{page.DisplayName}' 화면으로 접근 및 캡처 중...");

            try
            {
                await Task.Run(() =>
                {
                    // If page has a nav path, try navigating
                    if (page.NavPath.Count > 0)
                    {
                        ViewNavigator.NavigateTo(proc.Hwnd, page.NavPath, _project?.NavDelayMs ?? 1200,
                            CancellationToken.None, msg => BeginInvoke((Action)(() => lblStatus.Text = msg)));
                    }

                    string xamlText = File.Exists(page.ViewXamlPath) ? File.ReadAllText(page.ViewXamlPath) : null;
                    ViewNavigator.CaptureInto(page, proc.Hwnd, _tempDir, _project?.AllMenuNames(), xamlText);
                });

                page.Status = PageStatus.Captured;
                RenderViewDetail(page);
                PopulatePagesTree();
                lblStatus.Text = $"✅ '{page.DisplayName}' 캡처 완료 (Callouts: {page.Callouts.Count}개)";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ 캡처 실패: {ex.Message}";
                MessageBox.Show($"캡처 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, "");
            }
        }

        private async Task GeneratePageAsync(ManualPage page)
        {
            SetBusy(true, $"✨ '{page.DisplayName}' 매뉴얼 내용 LLM 요청 중...");
            try
            {
                await _viewGenerator.GenerateAsync(_project, page, CancellationToken.None);
                RenderViewDetail(page);
                PopulatePagesTree();
                lblStatus.Text = $"✅ '{page.DisplayName}' 매뉴얼 생성 완료";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ 생성 오류: {ex.Message}";
                MessageBox.Show($"LLM 생성 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, "");
            }
        }

        // ── Main Action Handlers ──

        // ── Capture (Batch or Screen) ──
        private async void BtnCapture_Click(object sender, EventArgs e)
        {
            if (cmbProcess.SelectedIndex < 0 || _processes.Count == 0)
            { lblStatus.Text = "⚠️ 먼저 대상 프로세스를 선택하세요."; return; }

            var proc = _processes[cmbProcess.SelectedIndex];

            // 1) View-Centric Mode: Crawl registered XML pages
            if (cmbMode.SelectedIndex == 0)
            {
                if (_project == null)
                {
                    MessageBox.Show("먼저 📂 Open XML 버튼으로 UI XML 파일을 열어주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var pagesToCap = _project.AllPages().Where(p => p.HasView && p.IsIncluded).ToList();
                if (pagesToCap.Count == 0)
                {
                    MessageBox.Show("접근할 View가 등록된 페이지가 없습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                SetBusy(true, $"🔍 {pagesToCap.Count}개 등록된 View 자동 접근 및 캡처 중...");
                FindBtn("btnCancel").Enabled = true;

                int done = 0;
                try
                {
                    foreach (var p in pagesToCap)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        lblStatus.Text = $"🎯 접근 중 ({++done}/{pagesToCap.Count}): {p.PathText}";
                        await Task.Run(() =>
                        {
                            ViewNavigator.NavigateTo(proc.Hwnd, p.NavPath, _project.NavDelayMs, _cts.Token);
                            string xamlText = File.Exists(p.ViewXamlPath) ? File.ReadAllText(p.ViewXamlPath) : null;
                            ViewNavigator.CaptureInto(p, proc.Hwnd, _tempDir, _project.AllMenuNames(), xamlText);
                        }, _cts.Token);
                        p.Status = PageStatus.Captured;
                        UpdateProgress(done, pagesToCap.Count);
                    }

                    PopulatePagesTree();
                    if (_selectedPage != null) RenderViewDetail(_selectedPage);
                    lblStatus.Text = $"✅ {done}개 View 캡처 완료! 이제 ✨ Generate로 LLM 매뉴얼 작성을 진행하세요.";
                }
                catch (OperationCanceledException)
                {
                    lblStatus.Text = "⏹ 자동 캡처가 취소되었습니다.";
                }
                catch (Exception ex)
                {
                    lblStatus.Text = $"⚠️ 캡처 중 오류: {ex.Message}";
                }
                finally
                {
                    SetBusy(false, "");
                    FindBtn("btnCancel").Enabled = false;
                }
                return;
            }

            // 2) Legacy Screen Mode (Current Screen / Full App)
            SetBusy(true, "🔍 UI 수집 중...");
            var mode = cmbMode.SelectedIndex == 2 ? CaptureMode.FullApp : CaptureMode.CurrentScreen;

            try
            {
                _snapshots = await Task.Run(() =>
                {
                    if (mode == CaptureMode.CurrentScreen)
                    {
                        var snap = UiInspector.CaptureCurrentScreen(proc.Hwnd, _tempDir);
                        return snap == null ? null : new List<WindowSnapshot> { snap };
                    }
                    return UiInspector.CaptureFullApp(proc.Hwnd, _tempDir,
                        msg => BeginInvoke((Action)(() => lblStatus.Text = msg)));
                });

                if (_snapshots == null || _snapshots.Count == 0)
                { lblStatus.Text = "⚠️ UI 요소를 수집하지 못했습니다."; SetBusy(false, ""); return; }

                // Source analysis
                string solDir = GetSolutionDirectory();
                _sourceAnalyzer = new SourceAnalyzer(solDir);
                if (!string.IsNullOrEmpty(solDir))
                {
                    lblStatus.Text = "📁 소스 분석 중...";
                    await Task.Run(() => _sourceAnalyzer.BuildIndex());
                }

                // Build document
                var mapper = new UiSourceMapper(_sourceAnalyzer);
                _document = mapper.BuildDocument(_snapshots, solDir, proc.Name);

                // Propagate raw screenshot paths to blocks
                foreach (var block in _document.Blocks.Where(b => b.Type == ManualBlockType.Screenshot))
                {
                    var snap = _snapshots.FirstOrDefault(sn =>
                        sn.WindowTitle == block.WindowName &&
                        (sn.TabContext ?? "") == (block.TabContext ?? ""));
                    if (snap != null)
                        block.RawScreenshotPath = snap.ScreenshotPath;
                }

                RenderCards();
                SetBusy(false, $"✅ {_snapshots.Count}개 화면, {_document.Elements.Count}개 UI 요소. ✨ Generate를 클릭하세요.");
                FindBtn("btnGenerate").Enabled = true;
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ 캡처 오류: {ex.Message}";
                SetBusy(false, "");
            }
        }

        // ── Generate (Batch) ──
        private async void BtnGenerate_Click(object sender, EventArgs e)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            FindBtn("btnCancel").Enabled = true;

            // View-Centric mode
            if (cmbMode.SelectedIndex == 0 && _project != null)
            {
                var pagesToGen = _project.AllPages().Where(p => p.IsIncluded && (p.HasScreenshot || p.HasSource)).ToList();
                if (pagesToGen.Count == 0)
                {
                    MessageBox.Show("생성할 페이지가 없습니다. (캡처 또는 소스가 있는 페이지 필요)", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                SetBusy(true, $"✨ {pagesToGen.Count}개 페이지 매뉴얼 LLM 생성 중...");
                int done = 0;
                try
                {
                    foreach (var p in pagesToGen)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        lblStatus.Text = $"✨ LLM 작성 중 ({++done}/{pagesToGen.Count}): {p.DisplayName}";
                        await _viewGenerator.GenerateAsync(_project, p, _cts.Token);
                        UpdateProgress(done, pagesToGen.Count);
                    }

                    PopulatePagesTree();
                    if (_selectedPage != null) RenderViewDetail(_selectedPage);
                    lblStatus.Text = $"✅ {done}개 페이지 매뉴얼 생성 완료! 내용을 확인/수정한 뒤 📄 Export DOCX를 클릭하세요.";
                    FindBtn("btnExport").Enabled = true;
                }
                catch (OperationCanceledException)
                { lblStatus.Text = "⏹ 생성이 취소됐습니다."; }
                catch (Exception ex)
                { lblStatus.Text = $"⚠️ 생성 오류: {ex.Message}"; }
                finally
                {
                    SetBusy(false, "");
                    FindBtn("btnCancel").Enabled = false;
                }
                return;
            }

            // Legacy Document mode
            if (_document == null) return;
            SetBusy(true, "✨ LLM 매뉴얼 생성 중...");

            try
            {
                await _generator.GenerateAllAsync(_document, _cts.Token);
                RenderCards();
                SetBusy(false, "✅ 매뉴얼 생성 완료. 각 카드에서 내용을 확인/편집 후 📄 Export DOCX를 클릭하세요.");
                FindBtn("btnExport").Enabled = true;
            }
            catch (OperationCanceledException)
            { lblStatus.Text = "⏹ 생성이 취소됐습니다."; }
            catch (Exception ex)
            { lblStatus.Text = $"⚠️ 오류: {ex.Message}"; }
            finally
            {
                SetBusy(false, "");
                FindBtn("btnCancel").Enabled = false;
            }
        }

        // ── Export (DOCX) ──
        private void BtnExport_Click(object sender, EventArgs e)
        {
            if (cmbMode.SelectedIndex == 0 && _project != null)
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "매뉴얼 Word 내보내기",
                    Filter = "Word Document (*.docx)|*.docx",
                    FileName = $"{_project.AppName}_Manual_{DateTime.Now:yyyyMMdd}.docx"
                })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    try
                    {
                        ManualExporter.ExportProject(_project, dlg.FileName, _tempDir);
                        lblStatus.Text = $"✅ DOCX 저장 완료: {Path.GetFileName(dlg.FileName)}";
                        if (MessageBox.Show("Word 파일을 열겠습니까?", "완료",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            System.Diagnostics.Process.Start(dlg.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"내보내기 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                return;
            }

            if (_document == null) return;

            // Before export, re-annotate screenshots with current callout state
            foreach (var block in _document.Blocks.Where(b => b.Type == ManualBlockType.Screenshot))
            {
                var snap = _snapshots?.FirstOrDefault(sn =>
                    sn.WindowTitle == block.WindowName &&
                    (sn.TabContext ?? "") == (block.TabContext ?? ""));

                if (snap != null && !string.IsNullOrEmpty(snap.ScreenshotPath))
                {
                    string tag = $"export_{block.Id}";
                    string annotated = ScreenCaptureEngine.AnnotateScreenshot(
                        snap.ScreenshotPath,
                        block.ShowCallouts ? block.Callouts : null,
                        snap.WindowRect, _tempDir, tag);
                    block.ScreenshotPath = annotated;
                }
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "매뉴얼 저장",
                Filter = "Word Document (*.docx)|*.docx",
                FileName = $"{_document.ApplicationName}_Manual_{DateTime.Now:yyyyMMdd}.docx"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    ManualExporter.ExportDocx(_document, dlg.FileName);
                    lblStatus.Text = $"✅ DOCX 저장 완료: {Path.GetFileName(dlg.FileName)}";
                    if (MessageBox.Show("Word 파일을 열겠습니까?", "완료",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        System.Diagnostics.Process.Start(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"저장 오류:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            _cts?.Cancel();
            FindBtn("btnCancel").Enabled = false;
            lblStatus.Text = "⏹ 취소 중...";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BLOCK MANIPULATION
        // ═══════════════════════════════════════════════════════════════════

        private void RemoveBlock(ManualBlock block)
        {
            if (_document == null) return;
            block.IsIncluded = false;
            RenderCards();
            lblStatus.Text = $"🗑 '{block.Title}' 제거됨 (Export에서 제외)";
        }

        private void MoveBlock(ManualBlock block, int delta)
        {
            if (_document == null) return;
            var visible = _document.Blocks.Where(b => b.IsIncluded).OrderBy(b => b.Order).ToList();
            int idx = visible.IndexOf(block);
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= visible.Count) return;

            // Swap orders
            int tmpOrder = block.Order;
            block.Order = visible[newIdx].Order;
            visible[newIdx].Order = tmpOrder;

            RenderCards();
        }

        private async Task RegenerateBlockAsync(ManualBlock block)
        {
            if (_document == null) return;
            var el    = _document.Elements.FirstOrDefault(e => e.UniqueId == block.ElementUniqueId);
            var trace = _document.Traces.FirstOrDefault(t => t.ElementUniqueId == block.ElementUniqueId);

            lblStatus.Text = $"🔄 Regenerating: {block.Title}...";
            await _generator.RegenerateBlockAsync(block, el, trace);
            RenderCards();
            lblStatus.Text = $"✅ Regenerated: {block.Title}";
        }

        private void AddManualStep()
        {
            if (_document == null) return;
            int maxOrder = _document.Blocks.Max(b => b.Order);

            _document.Blocks.Add(new ManualBlock
            {
                Type    = ManualBlockType.ControlItem,
                Title   = "New Step",
                Content = "(여기에 설명을 입력하세요)",
                Order   = maxOrder + 1,
                IsIncluded = true,
                Confidence = 1.0
            });
            RenderCards();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  DYNAMIC CALLOUT RENDERING
        // ═══════════════════════════════════════════════════════════════════

        private void RefreshScreenshotImage(Panel card, ManualBlock block, WindowSnapshot snapshot)
        {
            if (snapshot == null) return;
            var pic = FindControlByTag<PictureBox>(card, "pic");
            if (pic == null) return;

            var old = pic.Image;
            pic.Image = ScreenCaptureEngine.RenderAnnotatedBitmap(
                snapshot.ScreenshotPath,
                block.ShowCallouts ? block.Callouts : null,
                snapshot.WindowRect, block.ShowCallouts);
            old?.Dispose();
        }

        private static void RenumberCallouts(List<CalloutItem> callouts)
        {
            for (int i = 0; i < callouts.Count; i++)
                callouts[i].Number = i + 1;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════════

        private void RefreshProcessList()
        {
            _processes = UiInspector.GetWpfProcesses();
            cmbProcess.Items.Clear();
            foreach (var p in _processes)
                cmbProcess.Items.Add($"[{p.Pid}] {p.Name} — {Trunc(p.Title, 35)}");
            if (cmbProcess.Items.Count > 0) cmbProcess.SelectedIndex = 0;
        }

        private void SetBusy(bool busy, string msg)
        {
            progressBar.Visible = busy;
            progressBar.Style   = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!string.IsNullOrEmpty(msg)) lblStatus.Text = msg;
        }

        private void UpdateProgress(int done, int total)
        {
            progressBar.Style   = ProgressBarStyle.Continuous;
            progressBar.Maximum = Math.Max(1, total);
            progressBar.Value   = Math.Min(done, total);
            lblStatus.Text      = $"✨ 생성 중... {done}/{total}";
        }

        private string GetSolutionDirectory()
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string sol = dte?.Solution?.FullName;
                return string.IsNullOrEmpty(sol) ? null : Path.GetDirectoryName(sol);
            }
            catch { return null; }
        }

        private Control FindBtn(string tag)
        {
            foreach (Control c in pnlToolbar.Controls)
                if (c.Tag as string == tag) return c;
            return new Control(); // null-safe fallback
        }

        private static T FindControlByTag<T>(Panel parent, string tag) where T : Control
        {
            foreach (Control c in parent.Controls)
                if (c is T t && (c.Tag as string) == tag) return t;
            return null;
        }

        // ── Card painting ─────────────────────────────────────────────────

        private static void PaintCardBorder(Graphics g, Panel card, Color borderColor, int width)
        {
            using (var pen = new Pen(borderColor, width))
            {
                int r = 6; // corner radius
                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                var path = RoundedRect(rect, r);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ── Control factory ───────────────────────────────────────────────

        private ComboBox CreateCombo(int w)
        {
            var cmb = new ComboBox
            {
                Width = w, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CLR_INPUT, ForeColor = CLR_TEXT,
                FlatStyle = FlatStyle.Flat, Font = FNT_SMALL,
                Margin = new Padding(0, 2, 4, 0)
            };
            cmb.DropDown += (s, e) => AutoWidenCombo(cmb);
            return cmb;
        }

        private static void AddLabel(FlowLayoutPanel parent, string text, int topPad)
        {
            parent.Controls.Add(new Label
            {
                Text = text, AutoSize = true, ForeColor = CLR_TEXT_DIM,
                Font = FNT_SMALL, Padding = new Padding(0, topPad + 2, 2, 0)
            });
        }

        private static void AddSpacer(FlowLayoutPanel parent, int w)
        {
            parent.Controls.Add(new Panel { Width = w, Height = 1, BackColor = Color.Transparent });
        }

        private Button CreateAccentBtn(string text, Color bg)
        {
            var btn = new Button
            {
                Text = text, Height = 28, AutoSize = true,
                FlatStyle = FlatStyle.Flat, BackColor = bg,
                ForeColor = Color.White, Font = FNT_BTN,
                Margin = new Padding(3, 0, 3, 0), Padding = new Padding(10, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Button CreateIconBtn(string icon, string tooltip, Color bg)
        {
            var btn = new Button
            {
                Text = icon, Width = 28, Height = 28,
                FlatStyle = FlatStyle.Flat, BackColor = bg,
                ForeColor = CLR_TEXT, Font = FNT_BTN,
                Margin = new Padding(2, 0, 2, 0), Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(btn, tooltip);
            return btn;
        }

        private static Button CreateSmallBtn(string text, Color bg)
        {
            var btn = new Button
            {
                Text = text, Width = 26, Height = 22,
                FlatStyle = FlatStyle.Flat, BackColor = bg,
                ForeColor = Color.White, Font = new Font("Segoe UI", 7.5f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static void AutoWidenCombo(ComboBox cb)
        {
            int max = cb.Width;
            using (var g = cb.CreateGraphics())
                foreach (var item in cb.Items)
                {
                    int w = (int)g.MeasureString(item.ToString(), cb.Font).Width
                            + SystemInformation.VerticalScrollBarWidth + 4;
                    if (w > max) max = w;
                }
            cb.DropDownWidth = max;
        }

        private static string GetCircle(int n) =>
            n >= 1 && n <= 20 ? ((char)(0x2460 + n - 1)).ToString() : $"({n})";

        private static string Trunc(string t, int max) =>
            t?.Length > max ? t.Substring(0, max) + "…" : (t ?? "");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Dispose();
                try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}
