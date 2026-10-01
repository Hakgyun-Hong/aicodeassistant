using System;

namespace AIAssistant
{
    /// <summary>
    /// Status lifecycle for a SAM metric fix suggestion.
    /// </summary>
    public enum SamFixStatus
    {
        PendingAnalysis,
        Analyzing,
        Suggested,
        Accepted,
        Rejected,
        Failed,
        /// <summary>File has changed since analysis — snippet may no longer match.</summary>
        Stale,
        /// <summary>
        /// This metric type has no direct code location (e.g. ModuleCircularDependency).
        /// LLM provides architectural suggestion only; no auto-apply.
        /// </summary>
        ArchitectureOnly
    }

    /// <summary>
    /// Union data model for one SAM metric violation row.
    /// All metric types share this class; unused fields remain null/0.
    /// </summary>
    public sealed class SamTodoItem
    {
        // ─── Identity ──────────────────────────────────────────────────────
        public Guid   Id            { get; set; } = Guid.NewGuid();
        public string Metric        { get; set; } = "";   // e.g. "CC", "DC", "GV"
        public string MetricFullName{ get; set; } = "";   // e.g. "CyclomaticComplexity"
        public int    RowNo         { get; set; }

        // ─── Code Location ─────────────────────────────────────────────────
        public string RelativePath  { get; set; } = "";   // from SAM HTML
        public string FilePath      { get; set; } = "";   // resolved absolute path
        public int    LineNo        { get; set; }
        public int    LineEnd       { get; set; }         // for range metrics

        // ─── Common value fields ───────────────────────────────────────────
        public int    Value         { get; set; }
        public string Violation     { get; set; } = "";

        // ─── CyclomaticComplexity / NestedControlStatements ───────────────
        public string Entity        { get; set; }
        public string Signature     { get; set; }

        // ─── DuplicateCode ────────────────────────────────────────────────
        public string BlockKey      { get; set; }
        public int    LineCount     { get; set; }
        public int    Token         { get; set; }

        // ─── ModuleCircularDependency / DependencyComplexity ──────────────
        public string FromModule    { get; set; }
        public string ToModule      { get; set; }
        public int    RelationCount { get; set; }
        public string Mark          { get; set; }
        public string Key           { get; set; }
        public string ParentModule  { get; set; }
        public int    Complexity    { get; set; }
        public int    ModuleMaxFanOut{ get; set; }

        // ─── CouplingBetweenObjects / LinesOfCode ─────────────────────────
        public string Package       { get; set; }
        public string Class         { get; set; }
        public string ClassType     { get; set; }

        // ─── GlobalVariable ───────────────────────────────────────────────
        public string AccessingFunc { get; set; }

        // ─── PredefinedPreprocessor ───────────────────────────────────────
        public string Name          { get; set; }
        public string MacroType     { get; set; }
        public int    CodeLOC       { get; set; }
        public int    MacroCount    { get; set; }
        public int    PP_LOC        { get; set; }

        // ─── GodModule ────────────────────────────────────────────────────
        public string Module        { get; set; }
        public int    BLOC          { get; set; }
        public int    FunctionCnt   { get; set; }
        public int    BLOCxFunctionCnt{ get; set; }
        public string ModulePath    { get; set; }

        // ─── Status & LLM ─────────────────────────────────────────────────
        public SamFixStatus Status        { get; set; } = SamFixStatus.PendingAnalysis;
        public string FailureReason       { get; set; }
        public string LlmSummary         { get; set; }
        public string LlmExplanation     { get; set; }
        public string OriginalSnippet    { get; set; }
        public string ProposedSnippet    { get; set; }
        public string UnifiedDiff        { get; set; }
        public string RiskLevel          { get; set; }
        public string OriginalFileHash   { get; set; }

        public DateTimeOffset  CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? UpdatedAt { get; set; }

        // ─── Computed Display Properties ──────────────────────────────────

        public string ShortFilePath =>
            string.IsNullOrEmpty(RelativePath)
                ? (string.IsNullOrEmpty(FilePath) ? "" : System.IO.Path.GetFileName(FilePath))
                : System.IO.Path.GetFileName(RelativePath);

        /// <summary>True when this metric row has a direct source file + line number.</summary>
        public bool HasCodeLocation =>
            !string.IsNullOrEmpty(RelativePath) &&
            Metric != "MCD" &&   // ModuleCircularDependency
            Metric != "DepC";    // DependencyComplexity

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case SamFixStatus.PendingAnalysis:  return "⏳ Pending";
                    case SamFixStatus.Analyzing:        return "🔄 Analyzing";
                    case SamFixStatus.Suggested:        return "💡 Suggested";
                    case SamFixStatus.Accepted:         return "✅ Accepted";
                    case SamFixStatus.Rejected:         return "❌ Rejected";
                    case SamFixStatus.Failed:           return "⚠️ Failed";
                    case SamFixStatus.Stale:            return "🔒 Stale";
                    case SamFixStatus.ArchitectureOnly: return "🏛️ Arch.Only";
                    default: return Status.ToString();
                }
            }
        }

        public string LineDisplay =>
            LineEnd > 0 && LineEnd != LineNo
                ? $"{LineNo}–{LineEnd}"
                : LineNo > 0 ? LineNo.ToString() : "";
    }
}
