using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Automation;

namespace AIAssistant
{
    public sealed class WpfProcessInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public IntPtr Hwnd { get; set; }
    }

    public sealed class UiInspectorNode
    {
        public UiElementInfo Info { get; set; }
        public AutomationElement Element { get; set; }
        public List<UiInspectorNode> Children { get; set; } = new List<UiInspectorNode>();
    }

    /// <summary>
    /// UI Automation Client — inspects a running WPF process from outside.
    /// Uses System.Windows.Automation (UIAutomationClient.dll) via Control View Walker
    /// to suppress layout-only elements (Grid, Border, Panel, Decorator).
    ///
    /// Two modes:
    ///   CurrentScreen — single pass on the currently-visible window.
    ///   FullApp       — auto-crawl all windows + Tab/TabItem navigation.
    /// </summary>
    public static class UiInspector
    {
        // Layout-only control types to suppress (ControlType IDs)
        private static readonly HashSet<int> SuppressedTypes = new HashSet<int>
        {
            ControlType.Custom.Id,          // anonymous containers
            ControlType.Group.Id,           // GroupBox interior
            ControlType.Pane.Id,            // scroll/dock panes (often layout-only)
            ControlType.Separator.Id,
            ControlType.ScrollBar.Id,
            ControlType.Thumb.Id,
            ControlType.TitleBar.Id,
        };

        // ─── Public API ─────────────────────────────────────────────────────

        /// <summary>
        /// Get all WPF processes visible to the user (have at least one window).
        /// Returns WpfProcessInfo list.
        /// </summary>
        public static List<WpfProcessInfo> GetWpfProcesses()
        {
            var result = new List<WpfProcessInfo>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(proc.MainWindowTitle)) continue;

                    // Skip system processes
                    string lower = proc.ProcessName.ToLower();
                    if (lower == "devenv" || lower == "explorer" || lower == "taskhostw"
                        || lower == "searchui" || lower == "dwm") continue;

                    result.Add(new WpfProcessInfo
                    {
                        Pid   = proc.Id,
                        Name  = proc.ProcessName,
                        Title = proc.MainWindowTitle,
                        Hwnd  = proc.MainWindowHandle
                    });
                }
                catch { /* access denied — skip */ }
            }
            return result.OrderBy(p => p.Name).ToList();
        }

        /// <summary>
        /// View-centric capture: screenshot + UI element walk, WITHOUT automatic callouts.
        /// Callout selection is done afterwards by CalloutPlanner using View source context.
        /// </summary>
        public static WindowSnapshot CaptureWindowElements(IntPtr hwnd, string tempDir, string tag)
        {
            var root = SafeFromHandle(hwnd);
            if (root == null) return null;

            var snapshot = new WindowSnapshot
            {
                Handle      = hwnd,
                WindowTitle = SafeGet(root, AutomationElement.NameProperty, ""),
                WindowRect  = GetWindowRect(root)
            };
            snapshot.ScreenshotPath = ScreenCaptureEngine.CaptureWindow(hwnd, tempDir, tag);
            snapshot.Elements       = WalkControlView(root, null, 0);
            foreach (var el in snapshot.Elements) el.WindowTitle = snapshot.WindowTitle;
            return snapshot;
        }

        /// <summary>
        /// CurrentScreen mode: collect UI elements from the given window handle (single pass).
        /// </summary>
        public static WindowSnapshot CaptureCurrentScreen(IntPtr hwnd, string tempDir)
        {
            var root = SafeFromHandle(hwnd);
            if (root == null) return null;

            var snapshot = new WindowSnapshot
            {
                Handle = hwnd,
                WindowTitle = SafeGet(root, AutomationElement.NameProperty, "")
            };

            // Capture screenshot first (before tree walk to minimize delay)
            snapshot.ScreenshotPath = ScreenCaptureEngine.CaptureWindow(hwnd, tempDir, "screen_raw");
            snapshot.WindowRect     = GetWindowRect(root);

            // Walk UI tree
            snapshot.Elements = WalkControlView(root, null, 0);

            // Score importance + assign callout numbers
            AssignCallouts(snapshot);

            // Annotate screenshot
            snapshot.AnnotatedPath = ScreenCaptureEngine.AnnotateScreenshot(
                snapshot.ScreenshotPath, snapshot.Callouts, snapshot.WindowRect, tempDir, "screen_ann");

            return snapshot;
        }

        /// <summary>
        /// FullApp mode: enumerate all windows of the process, auto-navigate tabs, collect all snapshots.
        /// </summary>
        public static List<WindowSnapshot> CaptureFullApp(IntPtr mainHwnd, string tempDir,
            Action<string> progress = null)
        {
            var snapshots = new List<WindowSnapshot>();

            // Step 1 — collect all top-level windows of this process
            int pid = GetProcessId(mainHwnd);
            var windows = GetAllWindowsForProcess(pid);

            int snapshotIndex = 0;
            foreach (var hwnd in windows)
            {
                var root = SafeFromHandle(hwnd);
                if (root == null) continue;

                string winTitle = SafeGet(root, AutomationElement.NameProperty, "");
                if (string.IsNullOrWhiteSpace(winTitle)) continue;

                progress?.Invoke($"Capturing: {winTitle}");

                // Capture the window at its current state
                string tag = $"win{snapshotIndex++}";
                var snap = CaptureSingleWindow(root, hwnd, winTitle, null, tag, tempDir);
                snapshots.Add(snap);

                // Auto-crawl all navigable elements (not just tabs)
                CrawlNavigableElements(root, hwnd, winTitle, pid, tempDir,
                    ref snapshotIndex, snapshots, progress);
            }

            return snapshots;
        }

        /// <summary>
        /// Crawls all types of navigable elements: TabItem, MenuItem, TreeItem, ListItem.
        /// Each activatable element is clicked → wait → capture.
        /// </summary>
        private static void CrawlNavigableElements(
            AutomationElement root, IntPtr hwnd, string winTitle, int pid,
            string tempDir, ref int snapshotIndex,
            List<WindowSnapshot> snapshots, Action<string> progress)
        {
            // ── Tab Items ──
            CrawlByType(root, hwnd, winTitle, ControlType.Tab, ControlType.TabItem,
                "Tab", tempDir, ref snapshotIndex, snapshots, progress);

            // ── Menu Items (top-level only, depth-1) ──
            CrawlMenuItems(root, hwnd, winTitle, tempDir, ref snapshotIndex, snapshots, progress);

            // ── TreeView Items (expanded nodes) ──
            CrawlByType(root, hwnd, winTitle, ControlType.Tree, ControlType.TreeItem,
                "Tree", tempDir, ref snapshotIndex, snapshots, progress);

            // ── ListBox / ListView Items ──
            CrawlByType(root, hwnd, winTitle, ControlType.List, ControlType.ListItem,
                "List", tempDir, ref snapshotIndex, snapshots, progress);

            // ── Check for newly opened child windows after navigation ──
            System.Threading.Thread.Sleep(300);
            var newWindows = GetAllWindowsForProcess(pid);
            foreach (var childHwnd in newWindows)
            {
                if (snapshots.Any(s => s.Handle == childHwnd)) continue;
                var childRoot = SafeFromHandle(childHwnd);
                if (childRoot == null) continue;
                string childTitle = SafeGet(childRoot, AutomationElement.NameProperty, "");
                if (string.IsNullOrWhiteSpace(childTitle)) continue;

                progress?.Invoke($"  Dialog: {childTitle}");
                string tag = $"win{snapshotIndex++}";
                var childSnap = CaptureSingleWindow(childRoot, childHwnd, childTitle,
                    $"Dialog from {winTitle}", tag, tempDir);
                snapshots.Add(childSnap);
            }
        }

        private static void CrawlByType(
            AutomationElement root, IntPtr hwnd, string winTitle,
            ControlType containerType, ControlType itemType, string label,
            string tempDir, ref int snapshotIndex,
            List<WindowSnapshot> snapshots, Action<string> progress)
        {
            var containers = FindAll(root, containerType);
            foreach (var container in containers)
            {
                var items = FindAll(container, itemType);
                foreach (var item in items)
                {
                    string itemName = SafeGet(item, AutomationElement.NameProperty, "");
                    if (string.IsNullOrWhiteSpace(itemName)) continue;

                    if (TrySelect(item) || TryInvoke(item))
                    {
                        System.Threading.Thread.Sleep(400);
                        progress?.Invoke($"  {label}: {winTitle} › {itemName}");

                        string tag = $"win{snapshotIndex++}";
                        var snap = CaptureSingleWindow(root, hwnd, winTitle, itemName, tag, tempDir);
                        snapshots.Add(snap);
                    }
                }
            }
        }

        private static void CrawlMenuItems(
            AutomationElement root, IntPtr hwnd, string winTitle,
            string tempDir, ref int snapshotIndex,
            List<WindowSnapshot> snapshots, Action<string> progress)
        {
            var menuBars = FindAll(root, ControlType.MenuBar);
            foreach (var menuBar in menuBars)
            {
                var menuItems = FindAll(menuBar, ControlType.MenuItem);
                foreach (var menuItem in menuItems)
                {
                    string name = SafeGet(menuItem, AutomationElement.NameProperty, "");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Expand the menu (don't click — just expand)
                    if (menuItem.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ecp))
                    {
                        try
                        {
                            ((ExpandCollapsePattern)ecp).Expand();
                            System.Threading.Thread.Sleep(300);
                            progress?.Invoke($"  Menu: {winTitle} › {name}");

                            string tag = $"win{snapshotIndex++}";
                            var snap = CaptureSingleWindow(root, hwnd, winTitle, $"Menu: {name}", tag, tempDir);
                            snapshots.Add(snap);

                            // Collapse back
                            ((ExpandCollapsePattern)ecp).Collapse();
                            System.Threading.Thread.Sleep(200);
                        }
                        catch { }
                    }
                }
            }
        }

        // ─── Private: single-window capture ──────────────────────────────────

        private static WindowSnapshot CaptureSingleWindow(AutomationElement root, IntPtr hwnd,
            string winTitle, string tabContext, string tag, string tempDir)
        {
            var snapshot = new WindowSnapshot
            {
                Handle      = hwnd,
                WindowTitle = winTitle,
                TabContext  = tabContext,
                WindowRect  = GetWindowRect(root)
            };
            snapshot.ScreenshotPath = ScreenCaptureEngine.CaptureWindow(hwnd, tempDir, $"{tag}_raw");
            snapshot.Elements = WalkControlView(root, null, 0);

            // Propagate tab/window context
            foreach (var el in snapshot.Elements)
            {
                el.WindowTitle = winTitle;
                el.TabContext  = tabContext;
            }

            AssignCallouts(snapshot);
            snapshot.AnnotatedPath = ScreenCaptureEngine.AnnotateScreenshot(
                snapshot.ScreenshotPath, snapshot.Callouts, snapshot.WindowRect,
                tempDir, $"{tag}_ann");
            return snapshot;
        }

        // ─── Private: UI tree walk ────────────────────────────────────────────

        private static List<UiElementInfo> WalkControlView(
            AutomationElement root, string parentId, int depth)
        {
            var results = new List<UiElementInfo>();
            if (root == null || depth > 30) return results;

            var walker = TreeWalker.ControlViewWalker;
            AutomationElement child;
            try { child = walker.GetFirstChild(root); }
            catch { return results; }

            while (child != null)
            {
                try
                {
                    var info = BuildElementInfo(child, parentId, depth);
                    if (info != null)
                    {
                        results.Add(info);
                        // Recurse
                        var descendants = WalkControlView(child, info.UniqueId, depth + 1);
                        results.AddRange(descendants);
                    }
                    else if (ShouldDescend(child))
                    {
                        // Suppressed container (Custom/Pane/Group — e.g. a Prism region UserControl):
                        // skip the container itself but keep walking its visible children.
                        results.AddRange(WalkControlView(child, parentId, depth + 1));
                    }
                }
                catch { /* skip corrupt elements */ }

                try { child = walker.GetNextSibling(child); }
                catch { break; }
            }
            return results;
        }

        private static bool ShouldDescend(AutomationElement el)
        {
            try
            {
                var cur = el.Current;
                return !cur.IsOffscreen && cur.BoundingRectangle.Width >= 2 && cur.BoundingRectangle.Height >= 2;
            }
            catch { return false; }
        }

        // ─── UI Tree Inspection (Full hierarchy for debugging / inspector UI) ───

        /// <summary>
        /// Inspects the complete UI Automation tree for a given window.
        /// Supports ControlView or RawView (all containers included).
        /// </summary>
        public static UiInspectorNode InspectTree(IntPtr hwnd, bool rawView = false, int maxDepth = 25)
        {
            var root = SafeFromHandle(hwnd);
            if (root == null) return null;
            return BuildNode(root, null, 0, rawView, maxDepth);
        }

        private static UiInspectorNode BuildNode(AutomationElement el, string parentId, int depth, bool rawView, int maxDepth)
        {
            if (el == null) return null;
            var info = BuildElementInfo(el, parentId, depth, includeSuppressed: true);
            var node = new UiInspectorNode
            {
                Element = el,
                Info = info
            };

            if (depth >= maxDepth) return node;

            var walker = rawView ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;
            AutomationElement child;
            try { child = walker.GetFirstChild(el); }
            catch { return node; }

            while (child != null)
            {
                try
                {
                    var childNode = BuildNode(child, info != null ? info.UniqueId : parentId, depth + 1, rawView, maxDepth);
                    if (childNode != null)
                        node.Children.Add(childNode);
                }
                catch { }

                try { child = walker.GetNextSibling(child); }
                catch { break; }
            }
            return node;
        }

        private static UiElementInfo BuildElementInfo(AutomationElement el, string parentId, int depth, bool includeSuppressed = false)
        {
            AutomationElement.AutomationElementInformation cur;
            try { cur = el.Current; }
            catch { return null; }

            // Skip offscreen elements unless suppressed are included
            if (!includeSuppressed && cur.IsOffscreen) return null;

            // Skip purely decorative types
            if (!includeSuppressed && SuppressedTypes.Contains(cur.ControlType.Id)) return null;

            // Skip zero-size elements
            var rect = cur.BoundingRectangle;
            if (!includeSuppressed && (rect.Width < 2 || rect.Height < 2)) return null;

            string typeName = cur.ControlType != null && !string.IsNullOrEmpty(cur.ControlType.ProgrammaticName)
                ? ToCamel(cur.ControlType.ProgrammaticName.Replace("ControlType.", ""))
                : "custom";

            var info = new UiElementInfo
            {
                Name          = cur.Name ?? "",
                AutomationId  = cur.AutomationId ?? "",
                ClassName     = cur.ClassName ?? "",
                ControlType   = typeName,
                IsEnabled     = cur.IsEnabled,
                IsOffscreen   = cur.IsOffscreen,
                HasKeyboardFocus = cur.HasKeyboardFocus,
                HelpText      = cur.HelpText ?? "",
                BoundingRectangle = new System.Windows.Rect(rect.Left, rect.Top, rect.Width, rect.Height),
                ParentId      = parentId,
                Depth         = depth
            };

            // ValuePattern
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
            {
                try { info.Value = ((ValuePattern)vp).Current.Value; } catch { }
            }

            // SupportedPatterns
            try
            {
                foreach (var pid in el.GetSupportedPatterns())
                    info.SupportedPatterns.Add(pid.ProgrammaticName.Replace("PatternIdentifiers.", "").Replace("Pattern", ""));
            }
            catch { }

            // ImportanceScore
            info.ImportanceScore = ComputeImportance(info, el);

            return info;
        }

        // ─── Importance scoring ───────────────────────────────────────────────

        private static int ComputeImportance(UiElementInfo info, AutomationElement el)
        {
            int score = 0;
            string ct = info.ControlType;

            // High-value interactive types
            if (ct == "button")        score += 4;
            if (ct == "comboBox")      score += 4;
            if (ct == "edit")          score += 3;
            if (ct == "checkBox")      score += 3;
            if (ct == "radioButton")   score += 3;
            if (ct == "listItem")      score += 2;
            if (ct == "tabItem")       score += 3;
            if (ct == "menuItem")      score += 3;
            if (ct == "dataItem")      score += 2;
            if (ct == "treeItem")      score += 2;
            if (ct == "slider")        score += 3;
            if (ct == "hyperlink")     score += 2;

            // Informational
            if (ct == "text")          score += 1;
            if (ct == "image")         score += 1;

            // Pattern-based bonuses
            if (info.SupportedPatterns.Contains("Invoke"))          score += 2;
            if (info.SupportedPatterns.Contains("Value"))           score += 1;
            if (info.SupportedPatterns.Contains("ExpandCollapse"))  score += 1;
            if (info.SupportedPatterns.Contains("Toggle"))          score += 1;

            // State bonuses
            if (!info.IsEnabled)       score -= 1;  // disabled but still worth noting
            if (!string.IsNullOrEmpty(info.Name)) score += 1;
            if (!string.IsNullOrEmpty(info.AutomationId)) score += 2;
            if (!string.IsNullOrEmpty(info.HelpText))  score += 1;

            // Penalty for anonymous/unnamed elements
            if (string.IsNullOrEmpty(info.Name) && string.IsNullOrEmpty(info.AutomationId))
                score -= 2;

            return Math.Max(0, score);
        }

        // ─── Callout assignment ────────────────────────────────────────────────

        private static void AssignCallouts(WindowSnapshot snapshot)
        {
            // Sort by importance descending, then top-to-bottom
            var worthy = snapshot.Elements
                .Where(e => e.ImportanceScore >= 3 && !e.IsOffscreen)
                .OrderByDescending(e => e.ImportanceScore)
                .ThenBy(e => e.BoundingRectangle.Top)
                .ThenBy(e => e.BoundingRectangle.Left)
                .Take(25)  // max 25 callouts per screen (readability)
                .ToList();

            int num = 1;
            foreach (var el in worthy)
            {
                el.CalloutNumber = num;
                snapshot.Callouts.Add(new CalloutItem
                {
                    Number    = num,
                    ElementId = el.UniqueId,
                    Label     = string.IsNullOrEmpty(el.Name) ? el.ControlType : el.Name,
                    Rect      = el.BoundingRectangle
                });
                num++;
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        /// <summary>"ComboBox" → "comboBox" (matches the names used by IsInteractive / scoring).</summary>
        private static string ToCamel(string s) =>
            string.IsNullOrEmpty(s) ? "" : char.ToLowerInvariant(s[0]) + s.Substring(1);

        internal static List<IntPtr> GetAllWindowsForProcess(int pid)
        {
            var handles = new List<IntPtr>();
            // Use EnumWindows via user32
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint procId);
                if ((int)procId == pid && NativeMethods.IsWindowVisible(hwnd))
                    handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return handles;
        }

        internal static int GetProcessId(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }

        internal static AutomationElement SafeFromHandle(IntPtr hwnd)
        {
            try { return AutomationElement.FromHandle(hwnd); }
            catch { return null; }
        }

        private static T SafeGet<T>(AutomationElement el, AutomationProperty prop, T defaultValue)
        {
            try { return (T)el.GetCurrentPropertyValue(prop); }
            catch { return defaultValue; }
        }

        private static System.Windows.Rect GetWindowRect(AutomationElement el)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                return new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height);
            }
            catch { return default; }
        }

        private static List<AutomationElement> FindAll(AutomationElement root, ControlType type)
        {
            var result = new List<AutomationElement>();
            try
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
                foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, cond))
                    result.Add(el);
            }
            catch { }
            return result;
        }

        private static bool TryInvoke(AutomationElement el)
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object p))
            {
                try { ((InvokePattern)p).Invoke(); return true; }
                catch { }
            }
            return false;
        }

        private static bool TrySelect(AutomationElement el)
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p))
            {
                try { ((SelectionItemPattern)p).Select(); return true; }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Attempts to activate an element using available automation patterns (Invoke, Select, Toggle, Expand)
        /// or fallback mouse click on its center point.
        /// </summary>
        public static bool TryActivateElement(AutomationElement el, out string methodUsed)
        {
            methodUsed = null;
            if (el == null) return false;

            if (TryInvoke(el)) { methodUsed = "Invoke"; return true; }
            if (TrySelect(el)) { methodUsed = "Select"; return true; }

            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object tp))
            {
                try
                {
                    ((TogglePattern)tp).Toggle();
                    methodUsed = "Toggle";
                    return true;
                }
                catch { }
            }

            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ecp))
            {
                try
                {
                    ((ExpandCollapsePattern)ecp).Expand();
                    methodUsed = "Expand";
                    return true;
                }
                catch { }
            }

            // Fallback: mouse click on element bounding rectangle
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.Width > 2 && r.Height > 2)
                {
                    int cx = (int)(r.Left + r.Width / 2);
                    int cy = (int)(r.Top + r.Height / 2);
                    NativeMethods.SetCursorPos(cx, cy);
                    System.Threading.Thread.Sleep(40);
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, cx, cy, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(40);
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, cx, cy, 0, UIntPtr.Zero);
                    methodUsed = "MouseClick";
                    return true;
                }
            }
            catch { }

            return false;
        }
    }

    /// <summary>P/Invoke declarations for window enumeration.</summary>
    internal static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
        public const int  SW_RESTORE = 9;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }
}
