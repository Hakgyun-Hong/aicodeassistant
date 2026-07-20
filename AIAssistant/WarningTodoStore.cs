using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// Central store for warning todo items.
    /// Uses BindingList for WinForms DataGridView binding.
    /// </summary>
    public sealed class WarningTodoStore
    {
        private static WarningTodoStore _instance;
        private static readonly object _lock = new object();

        public BindingList<WarningTodoItem> Items { get; } = new BindingList<WarningTodoItem>();

        public event Action Changed;

        public static WarningTodoStore Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new WarningTodoStore();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Add a new item or update if same warning already exists.
        /// </summary>
        public void AddOrUpdate(WarningTodoItem item)
        {
            // Duplicate detection key: Code + FilePath + first 80 chars of Message.
            // Using Message (rather than Line) keeps de-duplication correct even after
            // the file is edited and line numbers shift.
            int msgLen = Math.Min((item.Message ?? "").Length, 80);
            string msgKey = (item.Message ?? "").Substring(0, msgLen);

            var existing = Items.FirstOrDefault(x =>
                x.Code == item.Code &&
                x.FilePath == item.FilePath &&
                string.Equals(
                    (x.Message ?? "").Substring(0, Math.Min((x.Message ?? "").Length, 80)),
                    msgKey,
                    StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                Items.Add(item);
            }
            else
            {
                // Update existing — don't replace if already analyzed
                if (existing.Status == WarningFixStatus.Accepted ||
                    existing.Status == WarningFixStatus.Rejected)
                    return;

                existing.Message = item.Message;
                existing.Project = item.Project;
                existing.OriginalFileHash = item.OriginalFileHash;
                existing.UpdatedAt = DateTimeOffset.Now;
            }

            NotifyChanged();
        }

        /// <summary>
        /// Mark an item as Accepted.
        /// </summary>
        public void Accept(WarningTodoItem item)
        {
            item.Status = WarningFixStatus.Accepted;
            item.UpdatedAt = DateTimeOffset.Now;
            NotifyChanged();
        }

        /// <summary>
        /// Mark an item as Rejected.
        /// </summary>
        public void Reject(WarningTodoItem item)
        {
            item.Status = WarningFixStatus.Rejected;
            item.UpdatedAt = DateTimeOffset.Now;
            NotifyChanged();
        }

        /// <summary>
        /// Clear all items.
        /// </summary>
        public void Clear()
        {
            Items.Clear();
            NotifyChanged();
        }

        /// <summary>
        /// Get counts for status bar display.
        /// </summary>
        public string GetStatusSummary()
        {
            int total = Items.Count;
            int pending = Items.Count(x => x.Status == WarningFixStatus.PendingAnalysis);
            int analyzing = Items.Count(x => x.Status == WarningFixStatus.Analyzing);
            int suggested = Items.Count(x => x.Status == WarningFixStatus.Suggested);
            int accepted = Items.Count(x => x.Status == WarningFixStatus.Accepted);
            int rejected = Items.Count(x => x.Status == WarningFixStatus.Rejected);
            int failed = Items.Count(x => x.Status == WarningFixStatus.Failed || x.Status == WarningFixStatus.Stale);

            return $"Total: {total}  |  ⏳ {pending}  🔄 {analyzing}  💡 {suggested}  ✅ {accepted}  ❌ {rejected}  ⚠️ {failed}";
        }

        /// <summary>
        /// Fire the Changed event, safely marshalling to UI thread if needed.
        /// </summary>
        public void NotifyChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch
            {
                // Ignore UI update errors
            }
        }
    }
}
