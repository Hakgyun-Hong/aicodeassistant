using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Forms;

namespace AIAssistant
{
    /// <summary>
    /// Interactive UI Automation Tree Inspector.
    /// Allows inspecting running WPF applications, navigating partial or full trees,
    /// searching elements by Name/AutomationId, testing invocations, and adding elements to manual pages.
    /// </summary>
    public sealed class UiaTreeInspectorDialog : Form
    {
        private static readonly Color CLR_BG          = Color.FromArgb(30, 30, 30);
        private static readonly Color CLR_PANEL       = Color.FromArgb(37, 37, 38);
        private static readonly Color CLR_CARD        = Color.FromArgb(45, 45, 48);
        private static readonly Color CLR_BORDER      = Color.FromArgb(60, 60, 60);
        private static readonly Color CLR_TEXT        = Color.FromArgb(212, 212, 212);
        private static readonly Color CLR_TEXT_DIM    = Color.FromArgb(140, 140, 140);
        private static readonly Color CLR_ACCENT      = Color.FromArgb(14, 99, 156);
        private static readonly Color CLR_ACCENT_HOV  = Color.FromArgb(17, 119, 187);
        private static readonly Color CLR_MATCH       = Color.FromArgb(255, 215, 0);   // Gold for search match
        private static readonly Color CLR_MATCH_BG    = Color.FromArgb(50, 45, 10);

        private static readonly Font  FNT_NORMAL      = new Font("Segoe UI", 9F, FontStyle.Regular);
        private static readonly Font  FNT_BOLD        = new Font("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font  FNT_TITLE       = new Font("Segoe UI", 10.5F, FontStyle.Bold);
        private static readonly Font  FNT_MONO        = new Font("Consolas", 8.5F, FontStyle.Regular);

        private readonly IntPtr _hwnd;
        private readonly string _winTitle;
        private readonly ManualPage _targetPage;
        private readonly List<ElementLite> _staticElements;

        private TreeView         tvTree;
        private TextBox          txtSearch;
        private Label            lblMatchCount;
        private Label            lblTargetInfo;
        private ComboBox         cmbViewMode;
        private ListView         lvProperties;
        private Button           btnTestClick;
        private Button           btnAddCallout;
        private Button           btnCopy;
        private Button           btnRefresh;
        private Label            lblActionStatus;

        private UiInspectorNode  _rootNode;
        private int              _totalNodes;

        public event Action<ElementLite> ElementAddedToManual;

        public UiaTreeInspectorDialog(
            IntPtr hwnd,
            string winTitle = null,
            ManualPage targetPage = null,
            string initialFilter = null)
        {
            _hwnd = hwnd;
            _winTitle = winTitle ?? (hwnd != IntPtr.Zero ? $"HWND 0x{hwnd.ToInt64():X}" : "(No Window)");
            _targetPage = targetPage;
            _staticElements = null;

            InitializeUi(initialFilter);
            LoadLiveTree(rawView: false);
        }

        public UiaTreeInspectorDialog(
            IEnumerable<ElementLite> snapshotElements,
            string winTitle = null,
            ManualPage targetPage = null,
            string initialFilter = null)
        {
            _hwnd = IntPtr.Zero;
            _winTitle = winTitle ?? "(Captured Snapshot)";
            _targetPage = targetPage;
            _staticElements = snapshotElements?.ToList() ?? new List<ElementLite>();

            InitializeUi(initialFilter);
            LoadStaticElements();
        }

        private void InitializeUi(string initialFilter)
        {
            Text = "🔍 UI Automation Tree Inspector — WPF 화면/트리 탐색기";
            Size = new Size(1060, 720);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CLR_BG;
            ForeColor = CLR_TEXT;
            Font = FNT_NORMAL;

            // ── Top Toolbar Panel ──
            var pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = CLR_PANEL,
                Padding = new Padding(12, 10, 12, 10)
            };
            pnlTop.Paint += (s, e) =>
            {
                using (var pen = new Pen(CLR_BORDER))
                    e.Graphics.DrawLine(pen, 0, pnlTop.Height - 1, pnlTop.Width, pnlTop.Height - 1);
            };

            lblTargetInfo = new Label
            {
                Text = $"🎯 대상 창: {_winTitle}",
                Font = FNT_TITLE,
                ForeColor = Color.White,
                Location = new Point(12, 10),
                AutoSize = true
            };
            pnlTop.Controls.Add(lblTargetInfo);

            // Row 2: Search & Mode Controls
            int y2 = 42;
            var lblFilter = new Label
            {
                Text = "🔍 검색 / 필터:",
                ForeColor = CLR_TEXT_DIM,
                Location = new Point(12, y2 + 4),
                AutoSize = true
            };
            pnlTop.Controls.Add(lblFilter);

            txtSearch = new TextBox
            {
                Location = new Point(110, y2 + 1),
                Width = 240,
                BackColor = CLR_CARD,
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = initialFilter ?? ""
            };
            txtSearch.TextChanged += (s, e) => ApplySearchFilter(txtSearch.Text);
            pnlTop.Controls.Add(txtSearch);

            var btnClearSearch = CreateButton("✖", 28, 24);
            btnClearSearch.Location = new Point(354, y2);
            btnClearSearch.Click += (s, e) => txtSearch.Clear();
            pnlTop.Controls.Add(btnClearSearch);

            lblMatchCount = new Label
            {
                Text = "",
                ForeColor = CLR_MATCH,
                Location = new Point(390, y2 + 4),
                AutoSize = true
            };
            pnlTop.Controls.Add(lblMatchCount);

            // View Mode Combo (Control View vs Raw View)
            if (_hwnd != IntPtr.Zero)
            {
                var lblMode = new Label
                {
                    Text = "탐색 뷰:",
                    ForeColor = CLR_TEXT_DIM,
                    Location = new Point(560, y2 + 4),
                    AutoSize = true
                };
                pnlTop.Controls.Add(lblMode);

                cmbViewMode = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(620, y2),
                    Width = 140,
                    BackColor = CLR_CARD,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                cmbViewMode.Items.Add("Control View (표준)");
                cmbViewMode.Items.Add("Raw View (전체)");
                cmbViewMode.SelectedIndex = 0;
                cmbViewMode.SelectedIndexChanged += (s, e) =>
                {
                    LoadLiveTree(cmbViewMode.SelectedIndex == 1);
                };
                pnlTop.Controls.Add(cmbViewMode);

                btnRefresh = CreateAccentButton("🔄 새로고침 (Live Crawl)");
                btnRefresh.Location = new Point(770, y2);
                btnRefresh.Width = 150;
                btnRefresh.Height = 26;
                btnRefresh.Click += (s, e) => LoadLiveTree(cmbViewMode.SelectedIndex == 1);
                pnlTop.Controls.Add(btnRefresh);
            }

            Controls.Add(pnlTop);

            // ── Main Split Container (Left: Tree, Right: Properties) ──
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 600,
                SplitterWidth = 6,
                BackColor = CLR_BG
            };

            // Left: TreeView & Tree Actions
            var pnlLeft = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 6, 10) };
            tvTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = CLR_PANEL,
                ForeColor = CLR_TEXT,
                LineColor = Color.FromArgb(80, 80, 80),
                BorderStyle = BorderStyle.FixedSingle,
                Font = FNT_NORMAL,
                HideSelection = false,
                ShowNodeToolTips = true
            };
            tvTree.AfterSelect += TvTree_AfterSelect;
            pnlLeft.Controls.Add(tvTree);

            var pnlTreeBtns = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Padding = new Padding(0, 8, 0, 0)
            };
            var btnExpandAll = CreateButton("➕ 모두 펼치기", 100, 26);
            btnExpandAll.Location = new Point(0, 6);
            btnExpandAll.Click += (s, e) => tvTree.ExpandAll();
            pnlTreeBtns.Controls.Add(btnExpandAll);

            var btnCollapseAll = CreateButton("➖ 모두 접기", 100, 26);
            btnCollapseAll.Location = new Point(106, 6);
            btnCollapseAll.Click += (s, e) => tvTree.CollapseAll();
            pnlTreeBtns.Controls.Add(btnCollapseAll);

            if (_hwnd != IntPtr.Zero)
            {
                var btnBringFront = CreateButton("🎯 앱 활성화 (Front)", 130, 26);
                btnBringFront.Location = new Point(212, 6);
                btnBringFront.Click += (s, e) => ViewNavigator.BringToFront(_hwnd);
                pnlTreeBtns.Controls.Add(btnBringFront);
            }

            pnlLeft.Controls.Add(pnlTreeBtns);
            split.Panel1.Controls.Add(pnlLeft);

            // Right: Property Details & Actions
            var pnlRight = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 10, 12, 10) };

            var lblPropTitle = new Label
            {
                Text = "📋 선택된 요소 속성 (Element Details)",
                Font = FNT_TITLE,
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                Height = 26
            };
            pnlRight.Controls.Add(lblPropTitle);

            lvProperties = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = CLR_PANEL,
                ForeColor = CLR_TEXT,
                BorderStyle = BorderStyle.FixedSingle,
                Font = FNT_MONO
            };
            lvProperties.Columns.Add("속성 (Property)", 140);
            lvProperties.Columns.Add("값 (Value)", 270);
            pnlRight.Controls.Add(lvProperties);

            // Action Panel at bottom of right pane
            var pnlActions = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 120,
                Padding = new Padding(0, 10, 0, 0)
            };

            btnTestClick = CreateAccentButton("🎯 테스트 클릭 (Invoke / Click)");
            btnTestClick.Width = 230;
            btnTestClick.Height = 30;
            btnTestClick.Location = new Point(0, 10);
            btnTestClick.Enabled = false;
            btnTestClick.Click += BtnTestClick_Click;
            pnlActions.Controls.Add(btnTestClick);

            btnAddCallout = CreateButton("➕ 매뉴얼 Callout으로 추가", 200, 30);
            btnAddCallout.Location = new Point(236, 10);
            btnAddCallout.Enabled = false;
            btnAddCallout.Click += BtnAddCallout_Click;
            pnlActions.Controls.Add(btnAddCallout);

            btnCopy = CreateButton("📋 모든 속성 복사", 140, 26);
            btnCopy.Location = new Point(0, 48);
            btnCopy.Click += BtnCopy_Click;
            pnlActions.Controls.Add(btnCopy);

            var btnCopyId = CreateButton("📋 AutomationId 복사", 140, 26);
            btnCopyId.Location = new Point(146, 48);
            btnCopyId.Click += (s, e) => CopyProperty("AutomationId");
            pnlActions.Controls.Add(btnCopyId);

            var btnCopyName = CreateButton("📋 Name 복사", 120, 26);
            btnCopyName.Location = new Point(292, 48);
            btnCopyName.Click += (s, e) => CopyProperty("Name");
            pnlActions.Controls.Add(btnCopyName);

            lblActionStatus = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(78, 201, 176),
                Font = FNT_NORMAL,
                Location = new Point(2, 82),
                AutoSize = true
            };
            pnlActions.Controls.Add(lblActionStatus);

            pnlRight.Controls.Add(pnlActions);
            split.Panel2.Controls.Add(pnlRight);

            Controls.Add(split);
        }

        // ── Tree Population ──

        private void LoadLiveTree(bool rawView)
        {
            if (_hwnd == IntPtr.Zero) return;

            tvTree.Nodes.Clear();
            lblMatchCount.Text = "⏳ 트리를 탐색하는 중입니다...";
            Application.DoEvents();

            try
            {
                _rootNode = UiInspector.InspectTree(_hwnd, rawView: rawView, maxDepth: 20);
                _totalNodes = 0;

                if (_rootNode != null)
                {
                    var rootTn = CreateTreeNodeFromNode(_rootNode);
                    tvTree.Nodes.Add(rootTn);
                    rootTn.Expand();
                    if (rootTn.Nodes.Count > 0)
                    {
                        foreach (TreeNode c in rootTn.Nodes) c.Expand();
                    }
                    lblMatchCount.Text = $"총 {_totalNodes}개 노드 탐색 완료";
                }
                else
                {
                    lblMatchCount.Text = "⚠️ UI Automation 트리를 가져오지 못했습니다.";
                }

                if (!string.IsNullOrEmpty(txtSearch.Text))
                    ApplySearchFilter(txtSearch.Text);
            }
            catch (Exception ex)
            {
                lblMatchCount.Text = $"⚠️ 오류: {ex.Message}";
            }
        }

        private void LoadStaticElements()
        {
            tvTree.Nodes.Clear();
            _totalNodes = 0;

            var rootTn = new TreeNode($"📂 {_winTitle} ({_staticElements.Count}개 요소)")
            {
                ForeColor = Color.White,
                NodeFont = FNT_BOLD
            };

            foreach (var el in _staticElements)
            {
                _totalNodes++;
                string label = FormatElementLabel(el.ControlType, el.Name, el.AutomationId);
                var tn = new TreeNode(label)
                {
                    Tag = el,
                    ForeColor = GetControlColor(el.ControlType, el.IsEnabled)
                };
                rootTn.Nodes.Add(tn);
            }

            tvTree.Nodes.Add(rootTn);
            rootTn.Expand();
            lblMatchCount.Text = $"총 {_totalNodes}개 요소 로드됨";

            if (!string.IsNullOrEmpty(txtSearch.Text))
                ApplySearchFilter(txtSearch.Text);
        }

        private TreeNode CreateTreeNodeFromNode(UiInspectorNode node)
        {
            _totalNodes++;
            var info = node.Info;
            string label = info != null
                ? FormatElementLabel(info.ControlType, info.Name, info.AutomationId)
                : "[Element]";

            var tn = new TreeNode(label)
            {
                Tag = node,
                ForeColor = info != null ? GetControlColor(info.ControlType, info.IsEnabled) : CLR_TEXT
            };

            foreach (var child in node.Children)
            {
                tn.Nodes.Add(CreateTreeNodeFromNode(child));
            }
            return tn;
        }

        private static string FormatElementLabel(string controlType, string name, string automationId)
        {
            string ct = string.IsNullOrEmpty(controlType) ? "Element" : controlType;
            if (!string.IsNullOrEmpty(name))
            {
                return string.IsNullOrEmpty(automationId)
                    ? $"[{ct}] \"{name}\""
                    : $"[{ct}] \"{name}\"  (id: {automationId})";
            }
            return string.IsNullOrEmpty(automationId)
                ? $"[{ct}]"
                : $"[{ct}]  (id: {automationId})";
        }

        private static Color GetControlColor(string controlType, bool isEnabled)
        {
            if (!isEnabled) return Color.FromArgb(120, 120, 120);
            string ct = controlType?.ToLowerInvariant() ?? "";
            if (ct == "button" || ct == "menuitem" || ct == "tabitem" || ct == "hyperlink")
                return Color.FromArgb(78, 201, 176); // Cyan / Interactive
            if (ct == "edit" || ct == "combobox" || ct == "checkbox" || ct == "radiobutton")
                return Color.FromArgb(220, 220, 170); // Warm yellow
            if (ct == "text" || ct == "label")
                return Color.FromArgb(212, 212, 212); // Standard text
            if (ct == "window" || ct == "pane" || ct == "group" || ct == "custom")
                return Color.FromArgb(150, 150, 170); // Containers
            return CLR_TEXT;
        }

        // ── Search & Filter ──

        private void ApplySearchFilter(string query)
        {
            query = query?.Trim() ?? "";
            int matchCount = 0;
            TreeNode firstMatch = null;

            tvTree.BeginUpdate();
            try
            {
                foreach (TreeNode tn in tvTree.Nodes)
                {
                    matchCount += FilterNodeRecursive(tn, query, ref firstMatch);
                }
            }
            finally
            {
                tvTree.EndUpdate();
            }

            if (string.IsNullOrEmpty(query))
            {
                lblMatchCount.Text = $"총 {_totalNodes}개 노드";
            }
            else
            {
                lblMatchCount.Text = $"'{query}' 검색 결과: {matchCount}개 일치";
                if (firstMatch != null)
                {
                    tvTree.SelectedNode = firstMatch;
                    firstMatch.EnsureVisible();
                }
            }
        }

        private int FilterNodeRecursive(TreeNode tn, string query, ref TreeNode firstMatch)
        {
            int matches = 0;
            bool isMatch = false;

            if (string.IsNullOrEmpty(query))
            {
                // Reset style
                var info = ExtractInfo(tn.Tag);
                tn.ForeColor = info != null ? GetControlColor(info.ControlType, info.IsEnabled) : CLR_TEXT;
                tn.BackColor = Color.Transparent;
                tn.NodeFont = FNT_NORMAL;
            }
            else
            {
                var info = ExtractInfo(tn.Tag);
                if (info != null)
                {
                    isMatch = (info.Name != null && info.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                              (info.AutomationId != null && info.AutomationId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                              (info.ControlType != null && info.ControlType.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                              (info.ClassName != null && info.ClassName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (isMatch)
                {
                    matches++;
                    tn.ForeColor = CLR_MATCH;
                    tn.BackColor = CLR_MATCH_BG;
                    tn.NodeFont = FNT_BOLD;
                    if (firstMatch == null) firstMatch = tn;

                    // Expand parent so match is visible
                    TreeNode p = tn.Parent;
                    while (p != null) { p.Expand(); p = p.Parent; }
                }
                else
                {
                    var baseInfo = ExtractInfo(tn.Tag);
                    tn.ForeColor = baseInfo != null ? GetControlColor(baseInfo.ControlType, baseInfo.IsEnabled) : CLR_TEXT;
                    tn.BackColor = Color.Transparent;
                    tn.NodeFont = FNT_NORMAL;
                }
            }

            foreach (TreeNode child in tn.Nodes)
            {
                matches += FilterNodeRecursive(child, query, ref firstMatch);
            }
            return matches;
        }

        // ── Details View ──

        private void TvTree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            lvProperties.Items.Clear();
            lblActionStatus.Text = "";

            var tag = e.Node?.Tag;
            if (tag == null)
            {
                btnTestClick.Enabled = false;
                btnAddCallout.Enabled = false;
                return;
            }

            var info = ExtractInfo(tag);
            var liveNode = tag as UiInspectorNode;

            if (info != null)
            {
                AddProp("Name", info.Name);
                AddProp("AutomationId", info.AutomationId);
                AddProp("ControlType", info.ControlType);
                AddProp("ClassName", info.ClassName);
                AddProp("IsEnabled", info.IsEnabled.ToString());
                AddProp("IsOffscreen", info.IsOffscreen.ToString());
                AddProp("HasKeyboardFocus", info.HasKeyboardFocus.ToString());
                AddProp("Bounds (X, Y, W, H)", $"{info.BoundingRectangle.X:F0}, {info.BoundingRectangle.Y:F0}, {info.BoundingRectangle.Width:F0}, {info.BoundingRectangle.Height:F0}");
                AddProp("Patterns", string.Join(", ", info.SupportedPatterns ?? new List<string>()));
                if (!string.IsNullOrEmpty(info.Value))
                    AddProp("Value", info.Value);
                if (!string.IsNullOrEmpty(info.HelpText))
                    AddProp("HelpText", info.HelpText);
                AddProp("Depth", info.Depth.ToString());
                AddProp("ImportanceScore", info.ImportanceScore.ToString());

                btnTestClick.Enabled = liveNode?.Element != null;
                btnAddCallout.Enabled = _targetPage != null;
            }
            else
            {
                btnTestClick.Enabled = false;
                btnAddCallout.Enabled = false;
            }
        }

        private void AddProp(string name, string val)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(val ?? "");
            lvProperties.Items.Add(item);
        }

        // ── Action Handlers ──

        private void BtnTestClick_Click(object sender, EventArgs e)
        {
            var node = tvTree.SelectedNode?.Tag as UiInspectorNode;
            if (node?.Element == null) return;

            try
            {
                lblActionStatus.Text = "⏳ 실행 중...";
                Application.DoEvents();

                if (UiInspector.TryActivateElement(node.Element, out string method))
                {
                    lblActionStatus.Text = $"✅ {method} 성공!";
                    lblActionStatus.ForeColor = Color.FromArgb(78, 201, 176);
                }
                else
                {
                    lblActionStatus.Text = "⚠️ 활성화 패턴/클릭을 수행하지 못했습니다.";
                    lblActionStatus.ForeColor = Color.FromArgb(240, 100, 100);
                }
            }
            catch (Exception ex)
            {
                lblActionStatus.Text = $"❌ 오류: {ex.Message}";
                lblActionStatus.ForeColor = Color.FromArgb(240, 100, 100);
            }
        }

        private void BtnAddCallout_Click(object sender, EventArgs e)
        {
            if (_targetPage == null) return;
            var tag = tvTree.SelectedNode?.Tag;
            var info = ExtractInfo(tag);
            if (info == null) return;

            // Create ElementLite
            var elLite = new ElementLite
            {
                Name = info.Name,
                AutomationId = info.AutomationId,
                ControlType = info.ControlType,
                Importance = Math.Max(3, info.ImportanceScore),
                IsEnabled = info.IsEnabled,
                X = info.BoundingRectangle.X,
                Y = info.BoundingRectangle.Y,
                W = info.BoundingRectangle.Width,
                H = info.BoundingRectangle.Height
            };

            // Assign new callout number
            int nextNum = _targetPage.Callouts.Count > 0 ? _targetPage.Callouts.Max(c => c.Number) + 1 : 1;
            var callout = new PageCallout
            {
                Number = nextNum,
                ElementId = elLite.Id,
                Label = !string.IsNullOrEmpty(info.Name) ? info.Name : info.AutomationId,
                X = info.BoundingRectangle.X,
                Y = info.BoundingRectangle.Y,
                W = info.BoundingRectangle.Width,
                H = info.BoundingRectangle.Height
            };
            _targetPage.Callouts.Add(callout);

            // Add to controls table
            var ctrlDesc = new ControlDesc
            {
                Callout = nextNum,
                Name = !string.IsNullOrEmpty(info.Name) ? info.Name : info.AutomationId,
                Description = $"{info.ControlType} 컨트롤 ({info.Name})"
            };
            _targetPage.Controls.Add(ctrlDesc);

            lblActionStatus.Text = $"✅ Callout #{nextNum} ('{ctrlDesc.Name}') 매뉴얼에 추가되었습니다!";
            lblActionStatus.ForeColor = Color.FromArgb(78, 201, 176);

            ElementAddedToManual?.Invoke(elLite);
        }

        private void BtnCopy_Click(object sender, EventArgs e)
        {
            var lines = new List<string>();
            foreach (ListViewItem item in lvProperties.Items)
            {
                lines.Add($"{item.Text}: {item.SubItems[1].Text}");
            }
            if (lines.Count > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                lblActionStatus.Text = "📋 모든 속성이 클립보드에 복사되었습니다.";
                lblActionStatus.ForeColor = Color.FromArgb(78, 201, 176);
            }
        }

        private void CopyProperty(string propName)
        {
            foreach (ListViewItem item in lvProperties.Items)
            {
                if (item.Text == propName)
                {
                    Clipboard.SetText(item.SubItems[1].Text);
                    lblActionStatus.Text = $"📋 {propName} ('{item.SubItems[1].Text}') 복사됨.";
                    lblActionStatus.ForeColor = Color.FromArgb(78, 201, 176);
                    return;
                }
            }
        }

        private static UiElementInfo ExtractInfo(object tag)
        {
            if (tag is UiInspectorNode n) return n.Info;
            if (tag is ElementLite el)
            {
                return new UiElementInfo
                {
                    Name = el.Name,
                    AutomationId = el.AutomationId,
                    ControlType = el.ControlType,
                    BoundingRectangle = el.Rect,
                    ImportanceScore = el.Importance,
                    IsEnabled = el.IsEnabled
                };
            }
            if (tag is UiElementInfo ui) return ui;
            return null;
        }

        // ── Controls Factory ──

        private static Button CreateButton(string text, int width, int height)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = CLR_CARD,
                ForeColor = CLR_TEXT,
                Font = FNT_NORMAL,
                Cursor = Cursors.Hand
            };
        }

        private static Button CreateAccentButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = CLR_ACCENT,
                ForeColor = Color.White,
                Font = FNT_BOLD,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = CLR_ACCENT_HOV;
            btn.MouseEnter += (s, e) => btn.BackColor = CLR_ACCENT_HOV;
            btn.MouseLeave += (s, e) => btn.BackColor = CLR_ACCENT;
            return btn;
        }
    }
}
