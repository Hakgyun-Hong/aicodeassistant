using System;
using System.Collections.Generic;
using System.Drawing;

namespace AIAssistant
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ManualModels.cs  —  All data models for the WPF Auto Manual Generator
    // ═══════════════════════════════════════════════════════════════════════

    // ─── Capture Mode ───────────────────────────────────────────────────────

    /// <summary>
    /// Two capture modes:
    ///  CurrentScreen — single snapshot of the currently-visible window.
    ///  FullApp       — auto-crawl all windows, tabs, and dialogs.
    /// </summary>
    public enum CaptureMode { CurrentScreen, FullApp }

    // ─── UI Element ─────────────────────────────────────────────────────────

    /// <summary>
    /// Normalized UI Automation element from the target WPF process.
    /// </summary>
    public sealed class UiElementInfo
    {
        // Identity
        public string UniqueId        { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name            { get; set; } = "";
        public string AutomationId    { get; set; } = "";
        public string ClassName       { get; set; } = "";
        public string ControlType     { get; set; } = "";   // e.g. "button", "comboBox"

        // State
        public bool   IsEnabled       { get; set; }
        public bool   IsOffscreen     { get; set; }
        public bool   HasKeyboardFocus{ get; set; }
        public string Value           { get; set; }         // ValuePattern
        public string HelpText        { get; set; }
        public List<string> SupportedPatterns { get; set; } = new List<string>();

        // Position (physical screen coordinates)
        public System.Windows.Rect BoundingRectangle { get; set; }

        // Hierarchy
        public string ParentId        { get; set; }
        public List<UiElementInfo> Children { get; set; } = new List<UiElementInfo>();
        public int Depth              { get; set; }

        // Importance score — filters noise from layout-only elements
        public int ImportanceScore    { get; set; }

        // Source correlation (filled by UiSourceMapper)
        public string XamlFile        { get; set; }
        public int?   XamlLine        { get; set; }
        public string XName           { get; set; }
        public string CommandBinding  { get; set; }   // e.g. "StartCommand"
        public string ViewModelType   { get; set; }   // e.g. "MainViewModel"

        // Callout number assigned during screenshot annotation
        public int? CalloutNumber     { get; set; }

        // Window/screen context
        public string WindowTitle     { get; set; }
        public string TabContext      { get; set; }   // which tab this was captured under

        /// <summary>True for controls users actually interact with.</summary>
        public bool IsInteractive =>
            ControlType == "button" || ControlType == "comboBox" ||
            ControlType == "checkBox" || ControlType == "radioButton" ||
            ControlType == "edit" || ControlType == "listItem" ||
            ControlType == "tabItem" || ControlType == "menuItem" ||
            ControlType == "dataItem" || ControlType == "tree" ||
            ControlType == "treeItem" || ControlType == "slider" ||
            ControlType == "hyperlink";
    }

    // ─── Source Trace ────────────────────────────────────────────────────────

    /// <summary>
    /// Code trace for a single UI element:  AutomationId → XAML → Command → ViewModel → Method.
    /// </summary>
    public sealed class SourceTrace
    {
        public string ElementUniqueId     { get; set; }
        public string AutomationId        { get; set; }
        public string XName               { get; set; }

        // XAML location
        public string XamlFilePath        { get; set; }
        public int?   XamlLine            { get; set; }
        public string XamlFragment        { get; set; }

        // Binding
        public string BindingPath         { get; set; }   // e.g. "StartCommand"
        public string BindingType         { get; set; }   // "Command" | "Property"

        // ViewModel
        public string ViewModelTypeName   { get; set; }
        public string CommandPropertyName { get; set; }
        public string ExecuteMethodName   { get; set; }
        public string CanExecuteMethodName{ get; set; }
        public string CanExecuteCondition { get; set; }   // parsed condition text

        // Call graph (depth-first symbol chain)
        public List<CodeSymbol> CallGraph { get; set; } = new List<CodeSymbol>();

        // Source evidence for display in editor
        public List<string> Evidence      { get; set; } = new List<string>();
        // e.g. "MainWindow.xaml:183", "MainViewModel.cs:421"
    }

    public sealed class CodeSymbol
    {
        public string Kind   { get; set; }  // "Property" | "Method" | "Field"
        public string Symbol { get; set; }  // "MainViewModel.StartCommand"
        public string File   { get; set; }
        public int    Line   { get; set; }
    }

    // ─── Manual Block ─────────────────────────────────────────────────────────

    public enum ManualBlockType
    {
        Heading,        // H1/H2/H3 section title
        Description,    // General description paragraph
        Screenshot,     // Annotated screenshot with callouts
        ControlItem,    // Single UI control description
        Procedure,      // Step-by-step numbered list
        Warning,        // Warning/Note box
        Table           // Summary table
    }

    /// <summary>
    /// Atomic editable unit in the manual document.
    /// Multiple blocks compose a ManualSection; sections compose a ManualDocument.
    /// </summary>
    public sealed class ManualBlock
    {
        public string          Id            { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public ManualBlockType Type          { get; set; }
        public int             Order         { get; set; }
        public string          Title         { get; set; } = "";
        public string          Content       { get; set; } = "";   // main text / markdown
        public bool            IsIncluded    { get; set; } = true;

        // Screenshot block fields
        public string ScreenshotPath         { get; set; }   // path to annotated PNG
        public string RawScreenshotPath      { get; set; }   // path to raw (no annotations) PNG
        public List<CalloutItem> Callouts    { get; set; } = new List<CalloutItem>();
        public bool   ShowCallouts           { get; set; } = true;  // toggle callout visibility

        // Control item fields
        public string ElementUniqueId        { get; set; }
        public int?   CalloutNumber          { get; set; }

        // Source evidence
        public List<string> SourceEvidence   { get; set; } = new List<string>();
        public double       Confidence       { get; set; } = 1.0;

        // Procedure steps (for Procedure blocks)
        public List<string> Steps            { get; set; } = new List<string>();

        // Window/tab context
        public string WindowName             { get; set; }
        public string TabContext             { get; set; }

        // Heading level for Heading blocks (1–3). 0 = legacy behavior (Order 0 → H1, else H2).
        public int HeadingLevel              { get; set; }

        // Table blocks: first row = header
        public List<string[]> TableRows      { get; set; } = new List<string[]>();
    }

    public sealed class CalloutItem
    {
        public int    Number      { get; set; }
        public string ElementId   { get; set; }
        public string Label       { get; set; }
        public System.Windows.Rect Rect { get; set; }
    }

    // ─── Manual Document ──────────────────────────────────────────────────────

    /// <summary>
    /// Top-level document produced by the Auto Manual Generator.
    /// Contains all blocks and the annotated screenshot.
    /// </summary>
    public sealed class ManualDocument
    {
        public string Id              { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string Title           { get; set; } = "Software User Manual";
        public string ApplicationName { get; set; } = "";
        public string CapturedAt      { get; set; } = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
        public CaptureMode Mode       { get; set; }
        public string Subtitle        { get; set; }   // optional caption under the title (overrides default)

        // Ordered list of all blocks
        public List<ManualBlock> Blocks { get; set; } = new List<ManualBlock>();

        // All elements collected from UI Automation
        public List<UiElementInfo> Elements { get; set; } = new List<UiElementInfo>();

        // Code traces (one per element that has source correlation)
        public List<SourceTrace> Traces { get; set; } = new List<SourceTrace>();

        // Temp directory used for screenshots during this session
        public string TempDir { get; set; }
    }

    // ─── Window Snapshot ─────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot of a single window/tab at a specific application state.
    /// Used in Full App mode to represent each discovered screen.
    /// </summary>
    public sealed class WindowSnapshot
    {
        public string WindowTitle      { get; set; }
        public string TabContext       { get; set; }
        public IntPtr Handle           { get; set; }
        public string ScreenshotPath   { get; set; }   // raw screenshot (no annotations)
        public string AnnotatedPath    { get; set; }   // screenshot with callouts
        public bool   ShowCallouts     { get; set; } = true;
        public List<UiElementInfo> Elements { get; set; } = new List<UiElementInfo>();
        public List<CalloutItem>   Callouts { get; set; } = new List<CalloutItem>();
        public System.Windows.Rect WindowRect { get; set; }
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    }
}
