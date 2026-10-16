using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace AIAssistant
{
    /// <summary>
    /// Drives the target WPF app to a registered View by "clicking" the menu DisplayName path
    /// from the UI XML (e.g. CTC › EFEM), then captures the window.
    ///
    /// Activation order per element: Invoke → SelectionItem.Select → Toggle(Off→On) → Expand → real mouse click.
    /// Intermediate group nodes (no ViewName / not clickable) are skipped when their child is already visible.
    /// </summary>
    public static class ViewNavigator
    {
        public sealed class Result
        {
            public bool   Success;
            public string Message;
        }

        private static readonly HashSet<string> NavControlTypes = new HashSet<string>
        {
            ControlType.Button.ProgrammaticName, ControlType.MenuItem.ProgrammaticName,
            ControlType.TabItem.ProgrammaticName, ControlType.ListItem.ProgrammaticName,
            ControlType.TreeItem.ProgrammaticName, ControlType.RadioButton.ProgrammaticName,
            ControlType.Hyperlink.ProgrammaticName, ControlType.DataItem.ProgrammaticName,
            ControlType.CheckBox.ProgrammaticName
        };

        public static Result NavigateTo(IntPtr mainHwnd, IList<string> path, int leafDelayMs,
            CancellationToken ct, Action<string> log = null)
        {
            if (path == null || path.Count == 0)
                return new Result { Success = false, Message = "메뉴 경로가 비어 있습니다." };

            int pid = UiInspector.GetProcessId(mainHwnd);
            BringToFront(mainHwnd);

            for (int i = 0; i < path.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string name = path[i];
                bool isLeaf = i == path.Count - 1;

                // Skip a group node if the next level is already reachable.
                if (!isLeaf && FindTarget(pid, path[i + 1]) != null)
                {
                    log?.Invoke($"  ↳ '{name}' 건너뜀 (하위 메뉴가 이미 표시됨)");
                    continue;
                }

                var el = FindTarget(pid, name);
                if (el == null)
                {
                    if (isLeaf)
                        return new Result { Success = false, Message = $"'{name}' 버튼을 대상 앱에서 찾지 못했습니다." };
                    log?.Invoke($"  ↳ '{name}' 을(를) 찾지 못해 건너뜀");
                    continue;
                }

                string how = Activate(el);
                if (how == null)
                    return new Result { Success = false, Message = $"'{name}' 을(를) 클릭하지 못했습니다." };

                log?.Invoke($"  ↳ '{name}' {how}");
                Thread.Sleep(isLeaf ? Math.Max(200, leafDelayMs) : 450);
            }
            return new Result { Success = true, Message = "OK" };
        }

        /// <summary>Navigate (optional) + capture the main window into a ManualPage.</summary>
        public static void CaptureInto(ManualPage page, IntPtr mainHwnd, string tempDir,
            HashSet<string> menuNames, string viewXamlText)
        {
            BringToFront(mainHwnd);
            Thread.Sleep(150);

            string tag = $"page_{page.Id}_{DateTime.Now:HHmmssfff}";
            var snap = UiInspector.CaptureWindowElements(mainHwnd, tempDir, tag);
            if (snap == null || string.IsNullOrEmpty(snap.ScreenshotPath))
                throw new InvalidOperationException("창 캡처에 실패했습니다.");

            page.ScreenshotPath = snap.ScreenshotPath;
            page.WindowRect     = snap.WindowRect;
            page.CapturedAt     = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            page.Elements       = snap.Elements
                .Where(e => e.BoundingRectangle.Width >= 4 && e.BoundingRectangle.Height >= 4)
                .Select(ElementLite.From).ToList();

            var oldCallouts = page.Callouts;
            page.Callouts = CalloutPlanner.Suggest(page.Elements, menuNames, viewXamlText, page.WindowRect, page.DetailLevel);
            CalloutPlanner.SyncControlsAfterRecapture(page, oldCallouts);
        }

        /// <summary>
        /// Finds visible TabItem elements inside the specified window.
        /// </summary>
        public static List<AutomationElement> FindVisibleTabs(IntPtr mainHwnd)
        {
            var list = new List<AutomationElement>();
            var root = UiInspector.SafeFromHandle(mainHwnd);
            if (root == null) return list;

            try
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = root.FindAll(TreeScope.Descendants, cond);
                if (tabs != null)
                {
                    foreach (AutomationElement tab in tabs)
                    {
                        try
                        {
                            var cur = tab.Current;
                            if (!cur.IsOffscreen && cur.BoundingRectangle.Width >= 4 && cur.BoundingRectangle.Height >= 4)
                            {
                                string name = !string.IsNullOrWhiteSpace(cur.Name) ? cur.Name.Trim() : cur.AutomationId;
                                if (!string.IsNullOrWhiteSpace(name))
                                    list.Add(tab);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Crawls visible TabItems inside the current view, creates child ManualPages for each tab,
        /// and captures them, enriching the page tree with tab sub-pages.
        /// </summary>
        public static List<ManualPage> CrawlTabsForPage(
            ManualPage parentPage, IntPtr mainHwnd, string tempDir,
            HashSet<string> menuNames, string viewXamlText,
            CancellationToken ct, Action<string> progress = null)
        {
            var results = new List<ManualPage>();
            if (parentPage == null) return results;

            var tabs = FindVisibleTabs(mainHwnd);
            if (tabs.Count <= 1) return results; // Single tab or none: no sub-page breakdown needed

            if (parentPage.Children == null)
                parentPage.Children = new List<ManualPage>();

            progress?.Invoke($"  📑 '{parentPage.DisplayName}' 내부 {tabs.Count}개 하위 탭 순회 중...");

            int tabIndex = 0;
            foreach (var tab in tabs)
            {
                ct.ThrowIfCancellationRequested();

                string tabName;
                try
                {
                    var cur = tab.Current;
                    tabName = !string.IsNullOrWhiteSpace(cur.Name) ? cur.Name.Trim() : cur.AutomationId;
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(tabName)) continue;

                progress?.Invoke($"  ↳ 탭 전환 ({++tabIndex}/{tabs.Count}): {tabName}");

                // Switch to the tab
                string act = Activate(tab);
                Thread.Sleep(350); // wait for WPF tab transition and rendering

                // Find existing child page or create new
                var childPage = parentPage.Children.FirstOrDefault(c => string.Equals(c.DisplayName, tabName, StringComparison.OrdinalIgnoreCase));
                if (childPage == null)
                {
                    var path = new List<string>(parentPage.NavPath ?? new List<string>()) { tabName };
                    childPage = new ManualPage
                    {
                        DisplayName  = tabName,
                        ViewName     = parentPage.ViewName,
                        ViewXamlPath = parentPage.ViewXamlPath,
                        ViewModelPath = parentPage.ViewModelPath,
                        IsHome       = false,
                        Depth        = parentPage.Depth + 1,
                        NavPath      = path,
                        DetailLevel  = parentPage.DetailLevel
                    };
                    parentPage.Children.Add(childPage);
                }

                // Capture the tab sub-page
                CaptureInto(childPage, mainHwnd, tempDir, menuNames, viewXamlText);
                childPage.Status = PageStatus.Captured;
                results.Add(childPage);
            }

            // Switch back to the first tab for clean state
            if (tabs.Count > 0)
            {
                try { Activate(tabs[0]); Thread.Sleep(200); } catch { }
            }

            return results;
        }

        // ─── Element search ─────────────────────────────────────────────────

        private static AutomationElement FindTarget(int pid, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            AutomationElement best = null;
            int bestScore = int.MinValue;

            foreach (var hwnd in UiInspector.GetAllWindowsForProcess(pid))
            {
                var root = UiInspector.SafeFromHandle(hwnd);
                if (root == null) continue;

                // 1. Exact Name match
                var found = SafeFindAll(root, new PropertyCondition(AutomationElement.NameProperty, name));
                EvaluateMatches(found, ref best, ref bestScore, 100);

                // 2. Exact AutomationId match
                if (bestScore < 80)
                {
                    var foundId = SafeFindAll(root, new PropertyCondition(AutomationElement.AutomationIdProperty, name));
                    EvaluateMatches(foundId, ref best, ref bestScore, 90);
                }

                // 3. Fallback: fuzzy/case-insensitive walk
                if (bestScore < 60)
                {
                    var walker = TreeWalker.ControlViewWalker;
                    WalkAndMatch(root, name, ref best, ref bestScore, walker, 0);
                }
            }
            return bestScore > -50 ? best : null;
        }

        private static AutomationElementCollection SafeFindAll(AutomationElement root, Condition cond)
        {
            try { return root.FindAll(TreeScope.Descendants, cond); }
            catch { return null; }
        }

        private static void EvaluateMatches(AutomationElementCollection coll, ref AutomationElement best, ref int bestScore, int bonus)
        {
            if (coll == null) return;
            foreach (AutomationElement el in coll)
            {
                var target = ClimbToActionable(el);
                int score = Score(target) + bonus;
                if (score > bestScore) { bestScore = score; best = target; }
            }
        }

        private static void WalkAndMatch(AutomationElement el, string query, ref AutomationElement best, ref int bestScore, TreeWalker walker, int depth)
        {
            if (el == null || depth > 15) return;
            try
            {
                var cur = el.Current;
                if (!cur.IsOffscreen)
                {
                    string n = cur.Name ?? "";
                    string aid = cur.AutomationId ?? "";
                    int matchBonus = 0;

                    if (string.Equals(n, query, StringComparison.OrdinalIgnoreCase)) matchBonus = 80;
                    else if (string.Equals(aid, query, StringComparison.OrdinalIgnoreCase)) matchBonus = 70;
                    else if (n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) matchBonus = 40;
                    else if (aid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) matchBonus = 35;

                    if (matchBonus > 0)
                    {
                        var target = ClimbToActionable(el);
                        int s = Score(target) + matchBonus;
                        if (s > bestScore) { bestScore = s; best = target; }
                    }
                }
            }
            catch { }

            AutomationElement child;
            try { child = walker.GetFirstChild(el); }
            catch { return; }

            while (child != null)
            {
                WalkAndMatch(child, query, ref best, ref bestScore, walker, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch { break; }
            }
        }

        /// <summary>Text inside a templated button → climb to the clickable ancestor (max 3 levels).</summary>
        private static AutomationElement ClimbToActionable(AutomationElement el)
        {
            var cur = el;
            for (int i = 0; i < 3 && cur != null; i++)
            {
                if (IsActionable(cur)) return cur;
                try { cur = TreeWalker.ControlViewWalker.GetParent(cur); }
                catch { break; }
            }
            return el;
        }

        private static bool IsActionable(AutomationElement el)
        {
            try
            {
                return el.TryGetCurrentPattern(InvokePattern.Pattern, out _) ||
                       el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) ||
                       el.TryGetCurrentPattern(TogglePattern.Pattern, out _) ||
                       el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
            }
            catch { return false; }
        }

        private static int Score(AutomationElement el)
        {
            try
            {
                var c = el.Current;
                if (c.IsOffscreen || c.BoundingRectangle.Width < 2) return -100;
                int s = 0;
                if (NavControlTypes.Contains(c.ControlType.ProgrammaticName)) s += 10;
                if (IsActionable(el)) s += 10;
                if (!c.IsEnabled) s -= 20;
                return s;
            }
            catch { return -100; }
        }

        // ─── Activation ─────────────────────────────────────────────────────

        private static string Activate(AutomationElement el)
        {
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
                { ((InvokePattern)ip).Invoke(); return "Invoke"; }

                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sp))
                { ((SelectionItemPattern)sp).Select(); return "Select"; }

                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object tp))
                {
                    var t = (TogglePattern)tp;
                    if (t.Current.ToggleState != ToggleState.On) t.Toggle();
                    return "Toggle";
                }

                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ep))
                {
                    var e = (ExpandCollapsePattern)ep;
                    if (e.Current.ExpandCollapseState != ExpandCollapseState.Expanded) e.Expand();
                    return "Expand";
                }
            }
            catch { /* fall through to mouse click */ }

            return MouseClick(el) ? "Click" : null;
        }

        private static bool MouseClick(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.Width < 1 || r.Height < 1) return false;
                int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
                NativeMethods.SetCursorPos(x, y);
                Thread.Sleep(40);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP,   0, 0, 0, UIntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        public static void BringToFront(IntPtr hwnd)
        {
            try
            {
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            catch { }
        }
    }

    /// <summary>
    /// Chooses which UI elements of a captured View deserve numbered callouts.
    ///   + elements whose Name / AutomationId appear in the View XAML (strong signal it belongs to this View)
    ///   − global navigation buttons (menu DisplayNames) and window chrome
    /// Numbering follows reading order (top→bottom, left→right) so the manual reads naturally.
    /// </summary>
    public static class CalloutPlanner
    {
        public static readonly HashSet<string> Chrome = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Minimize", "Maximize", "Close", "Restore", "최소화", "최대화", "닫기", "이전 크기로 복원", "System", "시스템" };

        public static readonly HashSet<string> InteractiveTypes = new HashSet<string>
        {
            "button", "comboBox", "checkBox", "radioButton", "edit", "tabItem", "menuItem",
            "slider", "hyperlink", "dataGrid", "list", "tree", "table", "spinner", "splitButton"
        };

        public static List<PageCallout> Suggest(List<ElementLite> elements, HashSet<string> menuNames,
            string viewXaml, System.Windows.Rect windowRect, ManualDetailLevel detailLevel = ManualDetailLevel.Normal)
        {
            int maxCallouts = detailLevel == ManualDetailLevel.Brief ? 8 :
                              detailLevel == ManualDetailLevel.Detailed ? 30 : 15;

            var scored = new List<(ElementLite El, int Score)>();
            foreach (var e in elements)
            {
                if (!e.IsVisible) continue; // Must be rendered and visible on screen
                if (!InteractiveTypes.Contains(e.ControlType ?? "")) continue;
                if (Chrome.Contains(e.Name ?? "")) continue;
                if (e.W * e.H < 64) continue;
                if (windowRect.Width > 0 && (e.W >= windowRect.Width * 0.95 && e.H >= windowRect.Height * 0.8)) continue;

                int s = e.Importance;

                // XAML definition boost
                bool inXaml = AppearsInXaml(viewXaml, e.Name) || AppearsInXaml(viewXaml, e.AutomationId);
                if (inXaml) s += 5;

                // Priority for major interactive controls: Buttons, Grids, Tables, Edits, Combos, Checks
                // (Independent of IsEnabled, since disabled buttons/grids also require documentation)
                if (e.ControlType == "button" || e.ControlType == "splitButton") s += 5;
                if (e.ControlType == "dataGrid" || e.ControlType == "table" || e.ControlType == "list" || e.ControlType == "tree") s += 5;
                if (e.ControlType == "comboBox" || e.ControlType == "edit" || e.ControlType == "checkBox" || e.ControlType == "radioButton") s += 3;

                bool isMenu = menuNames != null && !string.IsNullOrWhiteSpace(e.Name) && menuNames.Contains(e.Name.Trim());
                if (isMenu && !inXaml) continue;  // global navigation chrome, not part of this View

                int threshold = detailLevel == ManualDetailLevel.Detailed ? 1 :
                                detailLevel == ManualDetailLevel.Normal ? 2 : 3;
                if (s >= threshold) scored.Add((e, s));
            }

            // Drop near-duplicates (same rectangle reported by nested elements)
            var picked = new List<ElementLite>();
            foreach (var x in scored.OrderByDescending(t => t.Score))
            {
                if (picked.Any(p => Math.Abs(p.X - x.El.X) < 3 && Math.Abs(p.Y - x.El.Y) < 3 &&
                                    Math.Abs(p.W - x.El.W) < 6 && Math.Abs(p.H - x.El.H) < 6))
                    continue;
                picked.Add(x.El);
                if (picked.Count >= maxCallouts) break;
            }

            var ordered = ReadingOrder(picked);
            var result = new List<PageCallout>();
            for (int i = 0; i < ordered.Count; i++)
                result.Add(Make(ordered[i], i + 1));
            return result;
        }

        public static PageCallout Make(ElementLite e, int number) => new PageCallout
        {
            Number = number, ElementId = e.Id, Label = e.DisplayLabel,
            X = e.X, Y = e.Y, W = e.W, H = e.H
        };

        public static List<ElementLite> ReadingOrder(IEnumerable<ElementLite> items) =>
            items.OrderBy(e => (int)(e.Y / 24)).ThenBy(e => e.X).ToList();

        /// <summary>
        /// Renumber callouts in reading order and remap ControlDesc.Callout numbers accordingly.
        /// </summary>
        public static void Renumber(ManualPage page)
        {
            var ordered = page.Callouts.OrderBy(c => (int)(c.Y / 24)).ThenBy(c => c.X).ToList();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < ordered.Count; i++)
            {
                map[ordered[i].Number] = i + 1;
                ordered[i].Number = i + 1;
            }
            page.Callouts = ordered;
            foreach (var c in page.Controls)
            {
                if (!c.Callout.HasValue) continue;
                int n;
                c.Callout = map.TryGetValue(c.Callout.Value, out n) ? n : (int?)null;
            }
            page.Controls = page.Controls.OrderBy(c => c.Callout ?? int.MaxValue).ToList();
        }

        /// <summary>After recapture, keep descriptions whose control label still exists.</summary>
        public static void SyncControlsAfterRecapture(ManualPage page, List<PageCallout> oldCallouts)
        {
            if (page.Controls.Count == 0) return;
            var oldByNum = (oldCallouts ?? new List<PageCallout>())
                .GroupBy(c => c.Number).ToDictionary(g => g.Key, g => g.First().Label);
            foreach (var ctrl in page.Controls)
            {
                string label = ctrl.Callout.HasValue && oldByNum.ContainsKey(ctrl.Callout.Value)
                    ? oldByNum[ctrl.Callout.Value] : ctrl.Name;
                var match = page.Callouts.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
                ctrl.Callout = match?.Number;
            }
        }

        private static bool AppearsInXaml(string xaml, string token)
        {
            if (string.IsNullOrEmpty(xaml) || string.IsNullOrWhiteSpace(token) || token.Length < 2) return false;
            token = token.Trim();
            return xaml.IndexOf("\"" + token + "\"", StringComparison.Ordinal) >= 0 ||
                   xaml.IndexOf(">" + token + "<", StringComparison.Ordinal) >= 0;
        }
    }
}
