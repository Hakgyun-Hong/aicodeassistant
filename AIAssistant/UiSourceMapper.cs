using System;
using System.Collections.Generic;
using System.Linq;

namespace AIAssistant
{
    /// <summary>
    /// Core correlation engine: matches Runtime UI elements (from UiInspector)
    /// to XAML definitions (from SourceAnalyzer) and builds SourceTrace objects.
    ///
    /// Matching priority (highest first):
    ///   1. AutomationProperties.AutomationId  (explicit, stable identifier)
    ///   2. x:Name                             (design-time name, often same as AutomationId)
    ///   3. Automation Name + ControlType      (fallback for unlabeled elements)
    ///
    /// After matching, each element's CommandBinding / ViewModelType / XamlFile are set
    /// and a SourceTrace is built via SourceAnalyzer.
    ///
    /// This is the technical differentiator of the system — LLM alone cannot produce
    /// this mapping without the combined runtime + source evidence.
    /// </summary>
    public sealed class UiSourceMapper
    {
        private readonly SourceAnalyzer _analyzer;

        public UiSourceMapper(SourceAnalyzer analyzer)
        {
            _analyzer = analyzer;
        }

        /// <summary>
        /// For all elements in the snapshot, attempt to find XAML source and build traces.
        /// Modifies elements in-place (sets XamlFile, CommandBinding, ViewModelType, etc.)
        /// Returns the list of SourceTrace objects (one per element that was matched).
        /// </summary>
        public List<SourceTrace> MapSnapshot(WindowSnapshot snapshot)
        {
            var traces = new List<SourceTrace>();
            if (snapshot?.Elements == null) return traces;

            foreach (var el in snapshot.Elements)
            {
                // Only analyze elements with importance (ignore pure decorators)
                if (el.ImportanceScore < 2) continue;

                var trace = MapElement(el);
                if (trace != null)
                    traces.Add(trace);
            }

            Log($"Mapped {traces.Count}/{snapshot.Elements.Count} elements for '{snapshot.WindowTitle}'.");
            return traces;
        }

        /// <summary>
        /// Map a single UiElementInfo to its source. Returns null if no match found.
        /// </summary>
        public SourceTrace MapElement(UiElementInfo element)
        {
            // Match by AutomationId (highest priority)
            SourceAnalyzer.XamlElement xamlEl = null;

            if (!string.IsNullOrEmpty(element.AutomationId))
                xamlEl = _analyzer.FindByAutomationId(element.AutomationId);

            // Fallback: match by Name (used as x:Name or automation Name)
            if (xamlEl == null && !string.IsNullOrEmpty(element.Name))
                xamlEl = _analyzer.FindByAutomationId(element.Name);

            if (xamlEl != null)
            {
                // Populate element with XAML info
                element.XamlFile       = xamlEl.FilePath;
                element.XamlLine       = xamlEl.Line;
                element.XName          = xamlEl.XName;
                element.CommandBinding = xamlEl.CommandBinding;

                // Build detailed code trace
                var trace = _analyzer.BuildTrace(element);
                return trace;
            }

            return null;
        }

        /// <summary>
        /// Build a complete ManualDocument from one or more snapshots.
        /// Orchestrates the full pipeline: Map → Score → Group → Prepare blocks.
        /// </summary>
        public ManualDocument BuildDocument(
            List<WindowSnapshot> snapshots,
            string solutionDir,
            string appName)
        {
            var doc = new ManualDocument
            {
                Title           = $"{appName} — 사용자 매뉴얼",
                ApplicationName = appName,
                Mode            = snapshots.Count == 1 ? CaptureMode.CurrentScreen : CaptureMode.FullApp,
                TempDir         = snapshots.FirstOrDefault()?.ScreenshotPath != null
                                    ? System.IO.Path.GetDirectoryName(snapshots[0].ScreenshotPath)
                                    : System.IO.Path.GetTempPath()
            };

            int blockOrder = 0;

            // Title block
            doc.Blocks.Add(new ManualBlock
            {
                Type    = ManualBlockType.Heading,
                Title   = $"{appName} 사용자 매뉴얼",
                Content = $"캡처 일시: {doc.CapturedAt}",
                Order   = blockOrder++,
                IsIncluded = true
            });

            foreach (var snapshot in snapshots)
            {
                // Section header for each window/tab
                string sectionTitle = snapshot.TabContext != null
                    ? $"{snapshot.WindowTitle} › {snapshot.TabContext}"
                    : snapshot.WindowTitle;

                doc.Blocks.Add(new ManualBlock
                {
                    Type       = ManualBlockType.Heading,
                    Title      = sectionTitle,
                    Order      = blockOrder++,
                    WindowName = snapshot.WindowTitle,
                    TabContext = snapshot.TabContext
                });

                // Screenshot block
                if (!string.IsNullOrEmpty(snapshot.AnnotatedPath))
                {
                    var screenshotBlock = new ManualBlock
                    {
                        Type           = ManualBlockType.Screenshot,
                        Title          = $"{sectionTitle} — 화면 구성",
                        ScreenshotPath = snapshot.AnnotatedPath,
                        Callouts       = new List<CalloutItem>(snapshot.Callouts),
                        Order          = blockOrder++,
                        WindowName     = snapshot.WindowTitle,
                        TabContext     = snapshot.TabContext
                    };
                    doc.Blocks.Add(screenshotBlock);
                }

                // Map UI elements to source
                var traces = MapSnapshot(snapshot);
                doc.Traces.AddRange(traces);
                doc.Elements.AddRange(snapshot.Elements);

                // Build one ControlItem block per annotated element
                var worthy = snapshot.Elements
                    .Where(e => e.CalloutNumber.HasValue)
                    .OrderBy(e => e.CalloutNumber)
                    .ToList();

                foreach (var el in worthy)
                {
                    var trace = traces.FirstOrDefault(t => t.ElementUniqueId == el.UniqueId);

                    var block = new ManualBlock
                    {
                        Type          = ManualBlockType.ControlItem,
                        Title         = BuildElementTitle(el),
                        Content       = "",           // to be filled by ManualGenerator (LLM)
                        ElementUniqueId = el.UniqueId,
                        CalloutNumber   = el.CalloutNumber,
                        Order           = blockOrder++,
                        WindowName      = snapshot.WindowTitle,
                        TabContext      = snapshot.TabContext,
                        SourceEvidence  = trace?.Evidence ?? new List<string>()
                    };

                    doc.Blocks.Add(block);
                }
            }

            return doc;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string BuildElementTitle(UiElementInfo el)
        {
            if (!string.IsNullOrEmpty(el.Name)) return el.Name;
            if (!string.IsNullOrEmpty(el.AutomationId)) return el.AutomationId;
            return $"{CapitalizeFirst(el.ControlType)} ({el.UniqueId})";
        }

        private static string CapitalizeFirst(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][UiSourceMapper] {msg}");
    }
}
