using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AIAssistant
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ManualProject.cs — View-centric manual model (UI XML Menu → Prism View)
    //
    //  ManualProject
    //    └ ManualPage (one per <Menu> in the UI XML, tree-structured)
    //        ├ Source  : View XAML / code-behind / ViewModel (resolved via Prism)
    //        ├ Capture : screenshot + UIA elements + callouts
    //        └ Content : Summary / Controls / Procedures / Notes (LLM + user edit)
    //
    //  Persisted as  <name>.manual.json  +  <name>_assets/*.png
    // ═══════════════════════════════════════════════════════════════════════

    public enum PageStatus { New, Captured, Generated, Edited, Error }

    /// <summary>Per-page explanation detail level for LLM generation.</summary>
    public enum ManualDetailLevel
    {
        Brief,     // 간단히 (핵심 요약 및 주요 버튼 위주)
        Normal,    // 보통 (기본값: 표준 구성 및 주요 컨트롤)
        Detailed   // 상세히 (활성화된 모든 버튼, 그리드, 세부 동작 및 조건)
    }

    /// <summary>Lightweight, serializable UI element (from UI Automation).</summary>
    [DataContract]
    public sealed class ElementLite
    {
        [DataMember] public string Id           { get; set; }
        [DataMember] public string Name         { get; set; }
        [DataMember] public string AutomationId { get; set; }
        [DataMember] public string ControlType  { get; set; }
        [DataMember] public bool   IsEnabled    { get; set; }
        [DataMember] public bool   IsOffscreen  { get; set; }
        [DataMember] public int    Importance   { get; set; }
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
        [DataMember] public double W { get; set; }
        [DataMember] public double H { get; set; }

        public System.Windows.Rect Rect => new System.Windows.Rect(X, Y, Math.Max(0, W), Math.Max(0, H));

        /// <summary>Returns true if the element is rendered and visible on screen.</summary>
        public bool IsVisible => !IsOffscreen && W >= 2 && H >= 2;

        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(Name) ? Name.Trim() :
            !string.IsNullOrWhiteSpace(AutomationId) ? AutomationId : ControlType;

        public static ElementLite From(UiElementInfo e) => new ElementLite
        {
            Id           = e.UniqueId,
            Name         = e.Name,
            AutomationId = e.AutomationId,
            ControlType  = e.ControlType,
            IsEnabled    = e.IsEnabled,
            IsOffscreen  = e.IsOffscreen,
            Importance   = e.ImportanceScore,
            X = e.BoundingRectangle.X, Y = e.BoundingRectangle.Y,
            W = e.BoundingRectangle.Width, H = e.BoundingRectangle.Height
        };
    }

    /// <summary>Numbered callout on the page screenshot (screen coordinates).</summary>
    [DataContract]
    public sealed class PageCallout
    {
        [DataMember] public int    Number    { get; set; }
        [DataMember] public string ElementId { get; set; }
        [DataMember] public string Label     { get; set; }
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
        [DataMember] public double W { get; set; }
        [DataMember] public double H { get; set; }

        public System.Windows.Rect Rect => new System.Windows.Rect(X, Y, Math.Max(0, W), Math.Max(0, H));

        public CalloutItem ToCalloutItem() => new CalloutItem
        {
            Number = Number, ElementId = ElementId, Label = Label, Rect = Rect
        };
    }

    /// <summary>One row of the "컨트롤 설명" table. Public setters → DataGridView binding.</summary>
    [DataContract]
    public sealed class ControlDesc
    {
        [DataMember] public int?   Callout     { get; set; }
        [DataMember] public string Name        { get; set; } = "";
        [DataMember] public string Description { get; set; } = "";
        [DataMember] public string ElementId   { get; set; }
    }

    [DataContract]
    public sealed class ManualPage
    {
        // ── Identity / menu ──
        [DataMember] public string Id          { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        [DataMember] public string DisplayName { get; set; } = "";
        [DataMember] public string ViewName    { get; set; } = "";
        [DataMember] public bool   IsHome      { get; set; }
        [DataMember] public int    Depth       { get; set; }
        [DataMember] public List<string> NavPath  { get; set; } = new List<string>();
        [DataMember] public bool   IsIncluded  { get; set; } = true;
        [DataMember] public List<ManualPage> Children { get; set; } = new List<ManualPage>();

        // ── Tab sub-page (discovered by crawling visible TabItems inside the parent View) ──
        [DataMember] public bool   IsTabPage   { get; set; }
        [DataMember] public string TabName     { get; set; }

        // ── Source (resolved via PrismViewResolver, user-overridable) ──
        [DataMember] public string ViewTypeName      { get; set; }
        [DataMember] public string ViewXamlPath      { get; set; }
        [DataMember] public string ViewCodePath      { get; set; }
        [DataMember] public string ViewModelTypeName { get; set; }
        [DataMember] public string ViewModelPath     { get; set; }
        [DataMember] public string SourceNote        { get; set; }
        [DataMember] public bool   SourceLocked      { get; set; }   // true = user picked manually; don't auto-resolve

        // ── Capture ──
        [DataMember] public string ScreenshotPath { get; set; }
        [DataMember] public double WinX { get; set; }
        [DataMember] public double WinY { get; set; }
        [DataMember] public double WinW { get; set; }
        [DataMember] public double WinH { get; set; }
        [DataMember] public string CapturedAt   { get; set; }
        [DataMember] public bool   ShowCallouts { get; set; } = true;
        [DataMember] public List<ElementLite> Elements { get; set; } = new List<ElementLite>();
        [DataMember] public List<PageCallout> Callouts { get; set; } = new List<PageCallout>();

        // ── Content ──
        [DataMember] public string Summary    { get; set; } = "";
        [DataMember] public List<ControlDesc> Controls { get; set; } = new List<ControlDesc>();
        [DataMember] public string Procedures { get; set; } = "";
        [DataMember] public string Notes      { get; set; } = "";
        [DataMember] public string DetailLevelText { get; set; } = ManualDetailLevel.Normal.ToString();

        public ManualDetailLevel DetailLevel
        {
            get
            {
                ManualDetailLevel level;
                return Enum.TryParse(DetailLevelText, out level) ? level : ManualDetailLevel.Normal;
            }
            set { DetailLevelText = value.ToString(); }
        }

        // ── Status ──
        [DataMember] public string StatusText    { get; set; } = PageStatus.New.ToString();
        [DataMember] public string StatusMessage { get; set; }

        public PageStatus Status
        {
            get { PageStatus s; return Enum.TryParse(StatusText, out s) ? s : PageStatus.New; }
            set { StatusText = value.ToString(); }
        }

        public bool HasView       => !string.IsNullOrWhiteSpace(ViewName);
        public bool HasScreenshot => !string.IsNullOrEmpty(ScreenshotPath) && File.Exists(ScreenshotPath);
        public bool HasSource     => !string.IsNullOrEmpty(ViewXamlPath) || !string.IsNullOrEmpty(ViewModelPath);
        public bool HasContent    => !string.IsNullOrWhiteSpace(Summary) || Controls.Count > 0;

        public System.Windows.Rect WindowRect
        {
            get { return new System.Windows.Rect(WinX, WinY, Math.Max(0, WinW), Math.Max(0, WinH)); }
            set { WinX = value.X; WinY = value.Y; WinW = value.Width; WinH = value.Height; }
        }

        public string PathText => NavPath.Count > 0 ? string.Join(" › ", NavPath) : DisplayName;

        public List<CalloutItem> CalloutItems() => Callouts.Select(c => c.ToCalloutItem()).ToList();

        /// <summary>
        /// Synchronizes PageCallout list with current ControlDesc list:
        /// - Numbers in Callouts are updated to match Controls.
        /// - Labels in Callouts are updated to match Control names.
        /// - Bounding boxes are tracked by ElementId, Label, or Number without losing positions.
        /// - Any Callouts whose control has been removed are cleaned up.
        /// - New controls with numbers are correlated with page.Elements if available.
        /// </summary>
        public void SyncCalloutsFromControls()
        {
            if (Controls == null) Controls = new List<ControlDesc>();
            if (Callouts == null) Callouts = new List<PageCallout>();

            // NOTE: several callouts/controls may legitimately share an ElementId (LLM duplicates,
            // user-added rows, nested UIA elements). Never use ToDictionary here.
            var calloutsByEl = Callouts.Where(c => !string.IsNullOrEmpty(c.ElementId))
                                       .GroupBy(c => c.ElementId)
                                       .ToDictionary(g => g.Key, g => g.ToList());

            var updatedCallouts = new List<PageCallout>();
            var usedCallouts = new HashSet<PageCallout>();

            foreach (var ctrl in Controls)
            {
                if (!ctrl.Callout.HasValue || ctrl.Callout.Value <= 0) continue;
                int newNum = ctrl.Callout.Value;

                // 1. Try finding callout by ElementId (first one not yet claimed)
                PageCallout target = null;
                if (!string.IsNullOrEmpty(ctrl.ElementId) && calloutsByEl.TryGetValue(ctrl.ElementId, out var byEl))
                {
                    target = byEl.FirstOrDefault(c => !usedCallouts.Contains(c));
                }

                // 2. Try finding callout by Label matching ctrl.Name
                if (target == null && !string.IsNullOrEmpty(ctrl.Name))
                {
                    target = Callouts.FirstOrDefault(c => !usedCallouts.Contains(c) &&
                        string.Equals(c.Label, ctrl.Name, StringComparison.OrdinalIgnoreCase));
                }

                // 3. Try finding callout by existing Number
                if (target == null)
                {
                    target = Callouts.FirstOrDefault(c => !usedCallouts.Contains(c) && c.Number == newNum);
                }

                if (target != null)
                {
                    usedCallouts.Add(target);
                    target.Number = newNum;
                    if (!string.IsNullOrEmpty(ctrl.Name)) target.Label = ctrl.Name;
                    if (!string.IsNullOrEmpty(target.ElementId)) ctrl.ElementId = target.ElementId;
                    updatedCallouts.Add(target);
                }
                else
                {
                    // If no callout matched, find element in page.Elements
                    var el = Elements?.FirstOrDefault(e => !string.IsNullOrEmpty(ctrl.Name) &&
                        (string.Equals(e.Name, ctrl.Name, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.AutomationId, ctrl.Name, StringComparison.OrdinalIgnoreCase)));

                    if (el != null)
                    {
                        ctrl.ElementId = el.Id;
                        var newCo = new PageCallout
                        {
                            Number = newNum,
                            ElementId = el.Id,
                            Label = ctrl.Name,
                            X = el.X, Y = el.Y, W = el.W, H = el.H
                        };
                        usedCallouts.Add(newCo);
                        updatedCallouts.Add(newCo);
                    }
                }
            }

            Callouts = updatedCallouts;
        }

        // ── Editing helpers (used by ControlListEditor) ──

        /// <summary>Next free callout number.</summary>
        public int NextCalloutNumber() =>
            (Controls.Where(c => c.Callout.HasValue).Select(c => c.Callout.Value).DefaultIfEmpty(0).Max()) + 1;

        /// <summary>
        /// Assign a callout number to a control. If another control already uses it, the two numbers are
        /// swapped so numbering never collides.
        /// </summary>
        public void SetControlNumber(ControlDesc ctrl, int? number)
        {
            if (ctrl == null) return;
            if (number.HasValue && number.Value <= 0) number = null;
            if (number.HasValue)
            {
                var other = Controls.FirstOrDefault(c => !ReferenceEquals(c, ctrl) && c.Callout == number);
                if (other != null) other.Callout = ctrl.Callout;
            }
            ctrl.Callout = number;
            SyncCalloutsFromControls();
        }

        /// <summary>Move a control row up/down. Callout numbers follow the row order (swap).</summary>
        public int MoveControl(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= Controls.Count || target < 0 || target >= Controls.Count) return index;
            var a = Controls[index];
            var b = Controls[target];
            Controls[index] = b;
            Controls[target] = a;
            if (a.Callout.HasValue && b.Callout.HasValue)
            {
                int t = a.Callout.Value; a.Callout = b.Callout; b.Callout = t;
            }
            SyncCalloutsFromControls();
            return target;
        }

        /// <summary>Add a control row (+ callout) for a captured UI element. Returns the existing row if already present.</summary>
        public ControlDesc AddControlForElement(ElementLite el, bool withCallout = true)
        {
            if (el == null) return null;
            var existing = Controls.FirstOrDefault(c => c.ElementId == el.Id);
            if (existing != null) return existing;

            var ctrl = new ControlDesc
            {
                Callout     = withCallout ? (int?)NextCalloutNumber() : null,
                Name        = el.DisplayLabel,
                Description = el.IsEnabled ? "" : "(조건 충족 시 활성화)",
                ElementId   = el.Id
            };
            Controls.Add(ctrl);
            if (ctrl.Callout.HasValue)
                Callouts.Add(new PageCallout
                {
                    Number = ctrl.Callout.Value, ElementId = el.Id, Label = ctrl.Name,
                    X = el.X, Y = el.Y, W = el.W, H = el.H
                });
            SyncCalloutsFromControls();
            return ctrl;
        }

        /// <summary>
        /// Renumbers controls and callouts sequentially (1, 2, 3...) in reading/table order.
        /// </summary>
        public void RenumberControlsAndCallouts()
        {
            if (Controls == null || Controls.Count == 0) return;
            int seq = 1;

            foreach (var ctrl in Controls)
            {
                if (!string.IsNullOrWhiteSpace(ctrl.Name) || ctrl.Callout.HasValue)
                {
                    ctrl.Callout = seq++;
                }
            }

            SyncCalloutsFromControls();
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext ctx)
        {
            // DataContract serializers skip constructors/initializers → patch nulls.
            if (NavPath  == null) NavPath  = new List<string>();
            if (Children == null) Children = new List<ManualPage>();
            if (Elements == null) Elements = new List<ElementLite>();
            if (Callouts == null) Callouts = new List<PageCallout>();
            if (Controls == null) Controls = new List<ControlDesc>();
            if (Summary    == null) Summary    = "";
            if (Procedures == null) Procedures = "";
            if (Notes      == null) Notes      = "";
            if (DisplayName == null) DisplayName = "";
            if (ViewName    == null) ViewName    = "";
            if (string.IsNullOrEmpty(StatusText)) StatusText = PageStatus.New.ToString();
        }
    }

    [DataContract]
    public sealed class ManualProject
    {
        [DataMember] public string Title       { get; set; } = "사용자 매뉴얼";
        [DataMember] public string AppName     { get; set; } = "";
        [DataMember] public string UiXmlPath   { get; set; }
        [DataMember] public string SourceRoot  { get; set; }
        [DataMember] public int    NavDelayMs  { get; set; } = 1200;
        /// <summary>Draw numbered callouts on screenshots in the Word export (off by default: cleaner images).</summary>
        [DataMember] public bool   WordIncludeCallouts { get; set; }
        /// <summary>Crawl visible TabItems inside each View into tab sub-pages during capture.</summary>
        [DataMember] public bool   CrawlTabs   { get; set; } = true;
        [DataMember] public string CreatedAt   { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        [DataMember] public List<ManualPage> Roots { get; set; } = new List<ManualPage>();

        /// <summary>Path of the .manual.json file (null = never saved).</summary>
        public string ProjectPath { get; set; }

        public IEnumerable<ManualPage> AllPages()
        {
            var stack = new Stack<ManualPage>(Enumerable.Reverse(Roots));
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                yield return p;
                for (int i = p.Children.Count - 1; i >= 0; i--)
                    stack.Push(p.Children[i]);
            }
        }

        /// <summary>Pages that are effectively included (page AND all ancestors are checked).</summary>
        public IEnumerable<ManualPage> IncludedPages()
        {
            var stack = new Stack<ManualPage>(Enumerable.Reverse(Roots.Where(r => r.IsIncluded)));
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                yield return p;
                for (int i = p.Children.Count - 1; i >= 0; i--)
                    if (p.Children[i].IsIncluded) stack.Push(p.Children[i]);
            }
        }

        /// <summary>Parent of a page (null for roots).</summary>
        public ManualPage FindParent(ManualPage child)
        {
            foreach (var p in AllPages())
                if (p.Children.Contains(child)) return p;
            return null;
        }

        /// <summary>All menu display names — used to filter global navigation chrome from callouts.</summary>
        public HashSet<string> AllMenuNames() =>
            new HashSet<string>(AllPages().Select(p => p.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

        [OnDeserialized]
        private void OnDeserialized(StreamingContext ctx)
        {
            if (Roots == null) Roots = new List<ManualPage>();
            if (NavDelayMs <= 0) NavDelayMs = 1200;
            if (Title == null) Title = "사용자 매뉴얼";
        }
    }

    // ─── Persistence ────────────────────────────────────────────────────────

    public static class ManualProjectStore
    {
        public const string FileFilter = "Manual Project (*.manual.json)|*.manual.json|All files (*.*)|*.*";

        public static string AssetsDirFor(string projectPath)
        {
            string name = Path.GetFileName(projectPath);
            if (name.EndsWith(".manual.json", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".manual.json".Length);
            else
                name = Path.GetFileNameWithoutExtension(name);
            return Path.Combine(Path.GetDirectoryName(projectPath) ?? "", name + "_assets");
        }

        /// <summary>
        /// Save project JSON and copy screenshots into &lt;name&gt;_assets so the project is self-contained.
        /// </summary>
        public static void Save(ManualProject project, string path)
        {
            string assets = AssetsDirFor(path);
            Directory.CreateDirectory(assets);

            foreach (var page in project.AllPages())
            {
                if (!page.HasScreenshot) continue;
                string full = Path.GetFullPath(page.ScreenshotPath);
                if (full.StartsWith(Path.GetFullPath(assets), StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(assets, $"{page.Id}.png");
                File.Copy(full, dest, true);
                page.ScreenshotPath = dest;
            }

            var ser = new DataContractJsonSerializer(typeof(ManualProject));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true))
            {
                ser.WriteObject(writer, project);
                writer.Flush();
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            project.ProjectPath = path;
        }

        public static ManualProject Load(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(ManualProject));
            ManualProject project;
            using (var fs = File.OpenRead(path))
                project = (ManualProject)ser.ReadObject(fs);

            project.ProjectPath = path;

            // Re-anchor screenshots if the project folder was moved.
            string assets = AssetsDirFor(path);
            foreach (var page in project.AllPages())
            {
                if (string.IsNullOrEmpty(page.ScreenshotPath) || File.Exists(page.ScreenshotPath)) continue;
                string candidate = Path.Combine(assets, Path.GetFileName(page.ScreenshotPath));
                if (File.Exists(candidate)) page.ScreenshotPath = candidate;
            }
            return project;
        }
    }
}
