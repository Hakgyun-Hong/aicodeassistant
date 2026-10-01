using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// WPF Auto Manual Generator — main editor panel.
    ///
    /// Layout:
    ///   [Toolbar] Mode selector | Process ComboBox | Capture | Generate | Export DOCX | Cancel
    ///   ┌────────────────────┬────────────────────────────────────────┐
    ///   │  TreeView          │  TOP: annotated screenshot (PictureBox) │
    ///   │  (document blocks) │  BOT: block description (RichTextBox)  │
    ///   └────────────────────┴────────────────────────────────────────┘
    ///   [Status Bar]
    ///
    /// Two capture modes (toolbar toggle):
    ///   CurrentScreen — single snapshot of selected window
    ///   FullApp       — auto-navigate all windows + tabs
    /// </summary>
    public sealed class ManualEditorPanel : UserControl
    {
        // ─── Colors ──────────────────────────────────────────────────────────
        private static readonly Color BG     = Color.FromArgb(28, 28, 38);
        private static readonly Color BG2    = Color.FromArgb(35, 35, 48);
        private static readonly Color FG     = Color.FromArgb(220, 220, 230);
        private static readonly Color ACCENT = Color.FromArgb(86, 156, 214);
        private static readonly Color BORDER = Color.FromArgb(60, 60, 80);
        private static readonly Color GREEN  = Color.FromArgb(60, 120, 60);

        // ─── Controls ─────────────────────────────────────────────────────────
        private ComboBox        cmbProcess;
        private ComboBox        cmbMode;
        private Button          btnRefreshProcesses;
        private Button          btnCapture;
        private Button          btnGenerate;
        private Button          btnExport;
        private Button          btnCancel;
        private TreeView        treeDoc;
        private PictureBox      picScreenshot;
        private RichTextBox     txtDetail;
        private Label           lblStatus;
        private ProgressBar     progressBar;

        // ─── State ────────────────────────────────────────────────────────────
        private ManualDocument          _document;
        private List<WindowSnapshot>    _snapshots;
        private SourceAnalyzer          _sourceAnalyzer;
        private ManualGenerator         _generator;
        private CancellationTokenSource _cts;
        private string                  _tempDir;
        private bool                    _generating;

        // ─── Constructor ──────────────────────────────────────────────────────

        public ManualEditorPanel()
        {
            _generator = new ManualGenerator(maxParallel: 3);
            _generator.ProgressChanged += (done, total) =>
            {
                if (InvokeRequired) BeginInvoke((Action)(() => UpdateProgress(done, total)));
                else UpdateProgress(done, total);
            };

            _tempDir = Path.Combine(Path.GetTempPath(), "AIAssistant_Manual_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_tempDir);

            BuildUI();
            RefreshProcessList();
        }

        // ─── UI Construction ──────────────────────────────────────────────────

        private void BuildUI()
        {
            BackColor = BG;
            Padding   = Padding.Empty;

            // ── Toolbar ──
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 38,
                BackColor = BG2, Padding = new Padding(6, 5, 6, 5),
                WrapContents = false
            };

            // Mode selector
            var lblMode = new Label
            {
                Text = "Mode:", AutoSize = true, ForeColor = FG,
                Font = new Font("Segoe UI", 8.5f), Padding = new Padding(0, 5, 2, 0)
            };

            cmbMode = new ComboBox
            {
                Width = 130, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(0, 2, 12, 0)
            };
            cmbMode.Items.AddRange(new object[] { "📸 Current Screen", "🌐 Full App (Auto-Crawl)" });
            cmbMode.SelectedIndex = 0;
            cmbMode.DropDown += AutoWidenCombo;

            // Process selector
            var lblProc = new Label
            {
                Text = "Target:", AutoSize = true, ForeColor = FG,
                Font = new Font("Segoe UI", 8.5f), Padding = new Padding(0, 5, 2, 0)
            };

            cmbProcess = new ComboBox
            {
                Width = 220, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(0, 2, 4, 0)
            };
            cmbProcess.DropDown += AutoWidenCombo;

            btnRefreshProcesses = CreateToolBtn("🔄", BORDER);
            btnRefreshProcesses.Width = 28;
            btnRefreshProcesses.ToolTipText("Refresh process list");
            btnRefreshProcesses.Click += (s, e) => RefreshProcessList();

            btnCapture  = CreateToolBtn("📸 Capture",   ACCENT);
            btnGenerate = CreateToolBtn("✨ Generate",   Color.FromArgb(79, 60, 120));
            btnExport   = CreateToolBtn("📄 Export DOCX", GREEN);
            btnCancel   = CreateToolBtn("⏹ Cancel",     Color.FromArgb(100, 50, 50));

            btnCapture.Click  += BtnCapture_Click;
            btnGenerate.Click += BtnGenerate_Click;
            btnExport.Click   += BtnExport_Click;
            btnCancel.Click   += BtnCancel_Click;

            btnGenerate.Enabled = false;
            btnExport.Enabled   = false;
            btnCancel.Enabled   = false;

            toolbar.Controls.AddRange(new Control[]
                { lblMode, cmbMode, lblProc, cmbProcess, btnRefreshProcesses,
                  btnCapture, btnGenerate, btnExport, btnCancel });

            // ── Progress bar ──
            progressBar = new ProgressBar
            {
                Dock = DockStyle.Top, Height = 4,
                BackColor = BG, ForeColor = ACCENT,
                Style = ProgressBarStyle.Continuous, Visible = false
            };

            // ── Status bar ──
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom, Height = 22,
                Text = "WPF App을 실행하고 Target 프로세스를 선택한 후 📸 Capture를 클릭하세요.",
                BackColor = Color.FromArgb(20, 20, 30), ForeColor = Color.FromArgb(150, 150, 180),
                Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            // ── Main split: left tree 30% / right detail 70% ──
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                BackColor = BORDER, Panel1MinSize = 160, Panel2MinSize = 300
            };
            bool splitSet = false;
            mainSplit.Layout += (s, e) =>
            {
                if (!splitSet && mainSplit.Width > 100)
                {
                    mainSplit.SplitterDistance = Math.Max(180, (int)(mainSplit.Width * 0.30));
                    splitSet = true;
                }
            };

            // Left: Document tree
            treeDoc = new TreeView
            {
                Dock = DockStyle.Fill, BackColor = BG2, ForeColor = FG,
                BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9f),
                ShowLines = true, HideSelection = false,
                ItemHeight = 22
            };
            treeDoc.AfterSelect += TreeDoc_AfterSelect;

            // Context menu for tree nodes
            var ctxMenu = new ContextMenuStrip { BackColor = BG2, ForeColor = FG };
            ctxMenu.Items.Add("✏️ Edit Title",       null, (s, e) => EditSelectedTitle());
            ctxMenu.Items.Add("🔄 Regenerate",        null, async (s, e) => await RegenerateSelectedAsync());
            ctxMenu.Items.Add("👁 Include/Exclude",   null, (s, e) => ToggleSelectedInclude());
            ctxMenu.Items.Add("-");
            ctxMenu.Items.Add("⬆ Move Up",            null, (s, e) => MoveSelected(-1));
            ctxMenu.Items.Add("⬇ Move Down",          null, (s, e) => MoveSelected(+1));
            treeDoc.ContextMenuStrip = ctxMenu;

            mainSplit.Panel1.Controls.Add(treeDoc);

            // Right: top = screenshot, bottom = text
            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                BackColor = BORDER, Panel1MinSize = 80, Panel2MinSize = 100
            };
            bool rightSplitSet = false;
            rightSplit.Layout += (s, e) =>
            {
                if (!rightSplitSet && rightSplit.Height > 100)
                {
                    rightSplit.SplitterDistance = Math.Max(120, (int)(rightSplit.Height * 0.55));
                    rightSplitSet = true;
                }
            };

            picScreenshot = new PictureBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 28),
                SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.None
            };

            txtDetail = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = BG2, ForeColor = FG,
                Font = new Font("Segoe UI", 9.5f), ReadOnly = false,
                BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical
            };

            rightSplit.Panel1.Controls.Add(picScreenshot);
            rightSplit.Panel2.Controls.Add(txtDetail);
            mainSplit.Panel2.Controls.Add(rightSplit);

            // Assemble
            Controls.Add(mainSplit);
            Controls.Add(progressBar);
            Controls.Add(toolbar);
            Controls.Add(lblStatus);
        }

        // ─── Process list ─────────────────────────────────────────────────────

        private List<(int Pid, string Name, string Title, IntPtr Hwnd)> _processes
            = new List<(int, string, string, IntPtr)>();

        private void RefreshProcessList()
        {
            _processes = UiInspector.GetWpfProcesses();
            cmbProcess.Items.Clear();
            foreach (var p in _processes)
                cmbProcess.Items.Add($"[{p.Pid}] {p.Name} — {TruncateTitle(p.Title)}");

            if (cmbProcess.Items.Count > 0)
                cmbProcess.SelectedIndex = 0;
        }

        private static string TruncateTitle(string t, int max = 40)
            => t?.Length > max ? t.Substring(0, max) + "…" : (t ?? "");

        // ─── Capture ──────────────────────────────────────────────────────────

        private async void BtnCapture_Click(object sender, EventArgs e)
        {
            if (cmbProcess.SelectedIndex < 0 || _processes.Count == 0)
            {
                lblStatus.Text = "⚠️ 먼저 대상 프로세스를 선택하세요.";
                return;
            }

            SetBusy(true, "캡처 중...");

            var proc = _processes[cmbProcess.SelectedIndex];
            var mode = cmbMode.SelectedIndex == 1 ? CaptureMode.FullApp : CaptureMode.CurrentScreen;

            _snapshots = null;
            _document  = null;

            try
            {
                _snapshots = await Task.Run(() =>
                {
                    if (mode == CaptureMode.CurrentScreen)
                    {
                        var snap = UiInspector.CaptureCurrentScreen(proc.Hwnd, _tempDir);
                        return snap == null ? null : new List<WindowSnapshot> { snap };
                    }
                    else
                    {
                        return UiInspector.CaptureFullApp(proc.Hwnd, _tempDir,
                            msg => BeginInvoke((Action)(() => lblStatus.Text = msg)));
                    }
                });

                if (_snapshots == null || _snapshots.Count == 0)
                {
                    lblStatus.Text = "⚠️ UI 요소를 수집하지 못했습니다. 대상 앱이 실행 중인지 확인하세요.";
                    SetBusy(false, "");
                    return;
                }

                // Build source analyzer for VS solution
                string solDir = GetSolutionDirectory();
                _sourceAnalyzer = new SourceAnalyzer(solDir);
                if (!string.IsNullOrEmpty(solDir))
                {
                    lblStatus.Text = "소스 분석 중...";
                    await Task.Run(() => _sourceAnalyzer.BuildIndex());
                }

                // Map UI to source and build document
                var mapper = new UiSourceMapper(_sourceAnalyzer);
                _document  = mapper.BuildDocument(_snapshots, solDir, proc.Name);

                // Populate tree
                PopulateTree(_document);

                lblStatus.Text = $"✅ 캡처 완료. {_snapshots.Count}개 화면, " +
                                 $"{_document.Elements.Count}개 UI 요소 수집됨. " +
                                 $"이제 ✨ Generate를 클릭하여 매뉴얼을 생성하세요.";

                btnGenerate.Enabled = true;
                btnExport.Enabled   = false;
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ 캡처 오류: {ex.Message}";
            }
            finally
            {
                SetBusy(false, "");
            }
        }

        // ─── Generate ─────────────────────────────────────────────────────────

        private async void BtnGenerate_Click(object sender, EventArgs e)
        {
            if (_document == null) { lblStatus.Text = "먼저 캡처를 수행하세요."; return; }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            SetBusy(true, "LLM 매뉴얼 생성 중...");
            btnCancel.Enabled = true;

            try
            {
                await _generator.GenerateAllAsync(_document, _cts.Token);

                // Refresh tree (content updated)
                PopulateTree(_document);

                lblStatus.Text = $"✅ 매뉴얼 생성 완료. 📄 Export DOCX를 클릭하여 Word 파일로 저장하세요.";
                btnExport.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "⏹ 생성이 취소됐습니다.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"⚠️ 생성 오류: {ex.Message}";
            }
            finally
            {
                SetBusy(false, "");
                btnCancel.Enabled = false;
            }
        }

        // ─── Export DOCX ──────────────────────────────────────────────────────

        private void BtnExport_Click(object sender, EventArgs e)
        {
            if (_document == null) return;

            using (var dlg = new SaveFileDialog
            {
                Title    = "매뉴얼 저장",
                Filter   = "Word Document (*.docx)|*.docx",
                FileName = $"{_document.ApplicationName}_Manual_{DateTime.Now:yyyyMMdd}.docx"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    ManualExporter.ExportDocx(_document, dlg.FileName);
                    lblStatus.Text = $"✅ DOCX 저장 완료: {dlg.FileName}";

                    // Open in Word if available
                    if (MessageBox.Show("Word 파일을 열겠습니까?", "완료",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        System.Diagnostics.Process.Start(dlg.FileName);
                }
                catch (Exception ex)
                {
                    lblStatus.Text = $"⚠️ DOCX 저장 실패: {ex.Message}";
                    MessageBox.Show($"저장 오류:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            _cts?.Cancel();
            btnCancel.Enabled = false;
            lblStatus.Text = "⏹ 취소 중...";
        }

        // ─── Tree management ──────────────────────────────────────────────────

        private void PopulateTree(ManualDocument doc)
        {
            treeDoc.BeginUpdate();
            treeDoc.Nodes.Clear();

            TreeNode currentSection = null;

            foreach (var block in doc.Blocks.OrderBy(b => b.Order))
            {
                string prefix = block.IsIncluded ? "" : "🚫 ";
                string icon   = GetBlockIcon(block.Type);
                string label  = $"{icon}{prefix}{block.Title}";

                var node = new TreeNode(label) { Tag = block };
                StyleNode(node, block);

                if (block.Type == ManualBlockType.Heading)
                {
                    treeDoc.Nodes.Add(node);
                    currentSection = node;
                }
                else
                {
                    if (currentSection != null)
                        currentSection.Nodes.Add(node);
                    else
                        treeDoc.Nodes.Add(node);
                }
            }

            treeDoc.ExpandAll();
            treeDoc.EndUpdate();
        }

        private void StyleNode(TreeNode node, ManualBlock block)
        {
            if (!block.IsIncluded)
            {
                node.ForeColor = Color.FromArgb(80, 80, 100);
                return;
            }
            switch (block.Type)
            {
                case ManualBlockType.Heading:
                    node.ForeColor = Color.FromArgb(86, 156, 214); break;
                case ManualBlockType.Screenshot:
                    node.ForeColor = Color.FromArgb(150, 200, 150); break;
                case ManualBlockType.ControlItem:
                    node.ForeColor = FG; break;
                case ManualBlockType.Warning:
                    node.ForeColor = Color.FromArgb(220, 100, 50); break;
                default:
                    node.ForeColor = FG; break;
            }
        }

        private static string GetBlockIcon(ManualBlockType t)
        {
            switch (t)
            {
                case ManualBlockType.Heading:     return "📑 ";
                case ManualBlockType.Screenshot:  return "🖼 ";
                case ManualBlockType.ControlItem: return "🔘 ";
                case ManualBlockType.Procedure:   return "📋 ";
                case ManualBlockType.Warning:     return "⚠️ ";
                case ManualBlockType.Table:       return "📊 ";
                default:                          return "📝 ";
            }
        }

        private void TreeDoc_AfterSelect(object sender, TreeViewEventArgs e)
        {
            var block = e.Node?.Tag as ManualBlock;
            if (block == null) { picScreenshot.Image = null; txtDetail.Clear(); return; }

            // Show screenshot
            if (block.Type == ManualBlockType.Screenshot &&
                !string.IsNullOrEmpty(block.ScreenshotPath) &&
                File.Exists(block.ScreenshotPath))
            {
                try
                {
                    // Dispose old image
                    var old = picScreenshot.Image;
                    picScreenshot.Image = Image.FromFile(block.ScreenshotPath);
                    old?.Dispose();
                }
                catch { }
            }
            else if (block.Type == ManualBlockType.ControlItem)
            {
                // Find and show the screenshot for the same window/tab section
                var screenshotBlock = _document?.Blocks
                    .Where(b => b.Type == ManualBlockType.Screenshot &&
                                b.WindowName == block.WindowName &&
                                b.TabContext == block.TabContext)
                    .FirstOrDefault();

                if (screenshotBlock != null &&
                    !string.IsNullOrEmpty(screenshotBlock.ScreenshotPath) &&
                    File.Exists(screenshotBlock.ScreenshotPath))
                {
                    try
                    {
                        var old = picScreenshot.Image;
                        picScreenshot.Image = Image.FromFile(screenshotBlock.ScreenshotPath);
                        old?.Dispose();
                    }
                    catch { }
                }
            }
            else
            {
                picScreenshot.Image = null;
            }

            // Show detail text
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{block.Type}] {block.Title}");
            if (block.CalloutNumber.HasValue)
                sb.AppendLine($"Callout: #{block.CalloutNumber}");
            if (!string.IsNullOrEmpty(block.Content))
            {
                sb.AppendLine();
                sb.AppendLine(block.Content);
            }
            if (block.Steps?.Count > 0)
            {
                sb.AppendLine();
                for (int i = 0; i < block.Steps.Count; i++)
                    sb.AppendLine($"  {i+1}. {block.Steps[i]}");
            }
            if (block.SourceEvidence?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Source: {string.Join(", ", block.SourceEvidence)}");
            }
            if (block.Confidence > 0 && block.Confidence < 0.6)
                sb.AppendLine($"\n⚠️ 신뢰도 낮음 ({block.Confidence:P0}) — 검토 필요");

            txtDetail.Text = sb.ToString();

            // Allow editing description
            txtDetail.ReadOnly = false;
            txtDetail.Tag      = block;
            txtDetail.TextChanged -= TxtDetail_TextChanged;
            txtDetail.TextChanged += TxtDetail_TextChanged;
        }

        private void TxtDetail_TextChanged(object sender, EventArgs e)
        {
            // Save edits back to block.Content on change
            if (txtDetail.Tag is ManualBlock block)
                block.Content = txtDetail.Text;
        }

        // ─── Tree node actions ────────────────────────────────────────────────

        private void EditSelectedTitle()
        {
            if (treeDoc.SelectedNode?.Tag is ManualBlock block)
                treeDoc.SelectedNode.BeginEdit();
        }

        private void ToggleSelectedInclude()
        {
            if (treeDoc.SelectedNode?.Tag is ManualBlock block)
            {
                block.IsIncluded = !block.IsIncluded;
                StyleNode(treeDoc.SelectedNode, block);
                treeDoc.SelectedNode.Text = GetBlockIcon(block.Type) +
                    (block.IsIncluded ? "" : "🚫 ") + block.Title;
            }
        }

        private async Task RegenerateSelectedAsync()
        {
            if (treeDoc.SelectedNode?.Tag is ManualBlock block)
            {
                var el    = _document?.Elements.FirstOrDefault(e => e.UniqueId == block.ElementUniqueId);
                var trace = _document?.Traces.FirstOrDefault(t => t.ElementUniqueId == block.ElementUniqueId);

                lblStatus.Text = $"🔄 Regenerating: {block.Title}...";
                await _generator.RegenerateBlockAsync(block, el, trace);

                // Refresh tree selection
                TreeDoc_AfterSelect(this, new TreeViewEventArgs(treeDoc.SelectedNode));
                lblStatus.Text = $"✅ Regenerated: {block.Title}";
            }
        }

        private void MoveSelected(int delta)
        {
            if (_document == null) return;
            var block = treeDoc.SelectedNode?.Tag as ManualBlock;
            if (block == null) return;
            int idx = _document.Blocks.IndexOf(block);
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _document.Blocks.Count) return;

            _document.Blocks.RemoveAt(idx);
            _document.Blocks.Insert(newIdx, block);

            // Reassign order
            for (int i = 0; i < _document.Blocks.Count; i++)
                _document.Blocks[i].Order = i;

            PopulateTree(_document);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private void SetBusy(bool busy, string status)
        {
            _generating             = busy;
            btnCapture.Enabled      = !busy;
            btnGenerate.Enabled     = !busy && _document != null;
            btnExport.Enabled       = !busy && _document != null &&
                                      _document.Blocks.Any(b => !string.IsNullOrEmpty(b.Content));
            progressBar.Visible     = busy;
            progressBar.Style       = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;

            if (!string.IsNullOrEmpty(status))
                lblStatus.Text = status;
        }

        private void UpdateProgress(int done, int total)
        {
            progressBar.Style   = ProgressBarStyle.Continuous;
            progressBar.Maximum = Math.Max(1, total);
            progressBar.Value   = Math.Min(done, total);
            lblStatus.Text      = $"생성 중... {done}/{total}개 완료";
        }

        private string GetSolutionDirectory()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string solPath = dte?.Solution?.FullName;
                return string.IsNullOrEmpty(solPath) ? null : Path.GetDirectoryName(solPath);
            }
            catch { return null; }
        }

        private Button CreateToolBtn(string text, Color back)
        {
            var btn = new Button
            {
                Text = text, Height = 26, AutoSize = true,
                FlatStyle = FlatStyle.Flat, BackColor = back,
                ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(2, 0, 2, 0), Padding = new Padding(6, 0, 6, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static void AutoWidenCombo(object sender, EventArgs e)
        {
            var cb = (ComboBox)sender;
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

        // Dispose
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Dispose();
                picScreenshot.Image?.Dispose();
                // Clean up temp dir
                try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
                catch { }
            }
            base.Dispose(disposing);
        }
    }

    // Extension for tooltip on Button (avoids ToolTip control overhead)
    internal static class ButtonExtensions
    {
        public static void ToolTipText(this Button btn, string text)
        {
            var tt = new System.Windows.Forms.ToolTip();
            tt.SetToolTip(btn, text);
        }
    }
}
