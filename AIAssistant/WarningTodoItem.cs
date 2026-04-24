using System;

namespace AIAssistant
{
    /// <summary>
    /// Status lifecycle for a warning fix suggestion.
    /// </summary>
    public enum WarningFixStatus
    {
        PendingAnalysis,
        Analyzing,
        Suggested,
        Accepted,
        Rejected,
        Failed,
        Stale
    }

    /// <summary>
    /// Represents a single build warning with its LLM fix suggestion.
    /// </summary>
    public sealed class WarningTodoItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // Warning info from Error List
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; } = "";

        // Status
        public WarningFixStatus Status { get; set; } = WarningFixStatus.PendingAnalysis;

        // LLM suggestion
        public string LlmSummary { get; set; }
        public string LlmExplanation { get; set; }
        public string OriginalSnippet { get; set; }
        public string ProposedSnippet { get; set; }
        public string UnifiedDiff { get; set; }
        public string RiskLevel { get; set; }

        // Safety
        public string OriginalFileHash { get; set; }
        public string FailureReason { get; set; }

        // Timestamps
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary>
        /// Short display for the DataGridView file column.
        /// </summary>
        public string ShortFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(FilePath)) return "";
                return System.IO.Path.GetFileName(FilePath);
            }
        }

        /// <summary>
        /// Status emoji for display.
        /// </summary>
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case WarningFixStatus.PendingAnalysis: return "⏳ Pending";
                    case WarningFixStatus.Analyzing: return "🔄 Analyzing";
                    case WarningFixStatus.Suggested: return "💡 Suggested";
                    case WarningFixStatus.Accepted: return "✅ Accepted";
                    case WarningFixStatus.Rejected: return "❌ Rejected";
                    case WarningFixStatus.Failed: return "⚠️ Failed";
                    case WarningFixStatus.Stale: return "🔒 Stale";
                    default: return Status.ToString();
                }
            }
        }
    }
}
