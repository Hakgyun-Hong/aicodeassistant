using System;
using System.ComponentModel;
using System.Linq;

namespace AIAssistant
{
    /// <summary>
    /// Central store for SAM metric todo items.
    /// Uses BindingList for WinForms DataGridView binding.
    /// </summary>
    public sealed class SamTodoStore
    {
        private static SamTodoStore _instance;
        private static readonly object _lock = new object();

        public BindingList<SamTodoItem> Items { get; } = new BindingList<SamTodoItem>();

        public event Action Changed;

        public static SamTodoStore Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SamTodoStore();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Add a new item. Duplicate detection by Metric + RelativePath + LineNo.
        /// </summary>
        public void AddOrUpdate(SamTodoItem item)
        {
            var existing = Items.FirstOrDefault(x =>
                x.Metric       == item.Metric &&
                x.RelativePath == item.RelativePath &&
                x.LineNo       == item.LineNo &&
                x.RowNo        == item.RowNo);

            if (existing == null)
            {
                Items.Add(item);
            }
            else
            {
                // Don't overwrite already-processed items.
                if (existing.Status == SamFixStatus.Accepted ||
                    existing.Status == SamFixStatus.Rejected)
                    return;

                existing.UpdatedAt = DateTimeOffset.Now;
            }

            NotifyChanged();
        }

        public void Accept(SamTodoItem item)
        {
            item.Status    = SamFixStatus.Accepted;
            item.UpdatedAt = DateTimeOffset.Now;
            NotifyChanged();
        }

        public void Reject(SamTodoItem item)
        {
            item.Status    = SamFixStatus.Rejected;
            item.UpdatedAt = DateTimeOffset.Now;
            NotifyChanged();
        }

        public void Clear()
        {
            Items.Clear();
            NotifyChanged();
        }

        public string GetStatusSummary()
        {
            int total     = Items.Count;
            int pending   = Items.Count(x => x.Status == SamFixStatus.PendingAnalysis);
            int analyzing = Items.Count(x => x.Status == SamFixStatus.Analyzing);
            int suggested = Items.Count(x => x.Status == SamFixStatus.Suggested);
            int accepted  = Items.Count(x => x.Status == SamFixStatus.Accepted);
            int rejected  = Items.Count(x => x.Status == SamFixStatus.Rejected);
            int failed    = Items.Count(x => x.Status == SamFixStatus.Failed || x.Status == SamFixStatus.Stale);
            int archOnly  = Items.Count(x => x.Status == SamFixStatus.ArchitectureOnly);

            return $"Total: {total}  |  ⏳{pending}  🔄{analyzing}  💡{suggested}  ✅{accepted}  ❌{rejected}  ⚠️{failed}  🏛️{archOnly}";
        }

        public void NotifyChanged()
        {
            try { Changed?.Invoke(); }
            catch { }
        }
    }
}
