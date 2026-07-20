using System;
using System.IO;
using System.Text;

namespace AIAssistant
{
    /// <summary>
    /// Applies an accepted warning fix to the source file.
    /// Performs hash verification, snippet matching, backup creation, and text replacement.
    /// </summary>
    public static class WarningFixApplier
    {
        /// <summary>
        /// Apply the fix: verify hash → find snippet → backup → replace → update status.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public static string Apply(WarningTodoItem item)
        {
            if (item == null) return "No item selected.";

            if (string.IsNullOrWhiteSpace(item.FilePath))
                return "File path is empty.";

            if (!File.Exists(item.FilePath))
                return $"File not found: {item.FilePath}";

            if (string.IsNullOrWhiteSpace(item.OriginalSnippet))
                return "Original snippet is empty — cannot determine what to replace.";

            if (string.IsNullOrWhiteSpace(item.ProposedSnippet))
                return "Proposed snippet is empty — nothing to apply.";

            try
            {
                // 1. Hash verification — has the file changed since we analyzed it?
                if (!string.IsNullOrWhiteSpace(item.OriginalFileHash))
                {
                    string currentHash = WarningCollector.ComputeSha256(item.FilePath);
                    if (!string.Equals(currentHash, item.OriginalFileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = WarningFixStatus.Stale;
                        item.UpdatedAt = DateTimeOffset.Now;
                        return "File has been modified since the fix was suggested. Re-scan warnings to get a fresh suggestion.";
                    }
                }

                // 2. Read current file content
                string currentText = File.ReadAllText(item.FilePath, Encoding.UTF8);

                // 3. Snippet verification — does the original snippet still exist?
                // Normalize line endings for matching
                string normalizedText = currentText.Replace("\r\n", "\n");
                string normalizedSnippet = item.OriginalSnippet.Replace("\r\n", "\n").Trim();

                // Find the occurrence closest to the warning's line number.
                // This avoids mis-applying a fix when the same code pattern appears
                // in multiple places throughout the file.
                int preferredOffset = WarningFixApplierHelpers.GetLineStartOffset(normalizedText, item.Line);
                int index = WarningFixApplierHelpers.FindBestMatchIndex(normalizedText, normalizedSnippet, preferredOffset);
                if (index < 0)
                {
                    item.Status = WarningFixStatus.Stale;
                    item.UpdatedAt = DateTimeOffset.Now;
                    return "Original code snippet was not found in the current file. The file may have been edited.";
                }

                // 4. Create backup
                string backupPath = item.FilePath + ".warningfix.bak";
                File.Copy(item.FilePath, backupPath, true);

                // 5. Apply the replacement
                string proposedSnippet = item.ProposedSnippet.Replace("\r\n", "\n");
                string newText = normalizedText.Substring(0, index)
                               + proposedSnippet
                               + normalizedText.Substring(index + normalizedSnippet.Length);

                // Restore original line endings
                if (currentText.Contains("\r\n"))
                {
                    newText = newText.Replace("\n", "\r\n");
                }

                File.WriteAllText(item.FilePath, newText, Encoding.UTF8);

                // 6. Update status
                item.Status = WarningFixStatus.Accepted;
                item.UpdatedAt = DateTimeOffset.Now;

                return null; // Success
            }
            catch (Exception ex)
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = ex.Message;
                item.UpdatedAt = DateTimeOffset.Now;
                return $"Apply failed: {ex.Message}";
            }
        }
    }

    // Internal helpers used by WarningFixApplier
    internal static class WarningFixApplierHelpers
    {
        /// <summary>
        /// Returns the character offset in <paramref name="text"/> at which the
        /// 1-based <paramref name="targetLine"/> begins.
        /// </summary>
        internal static int GetLineStartOffset(string text, int targetLine)
        {
            if (targetLine <= 1) return 0;
            int lineNumber = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineNumber++;
                    if (lineNumber >= targetLine)
                        return i + 1;
                }
            }
            return text.Length;
        }

        /// <summary>
        /// Finds all occurrences of <paramref name="snippet"/> inside <paramref name="text"/>
        /// and returns the index of the occurrence whose start position is closest to
        /// <paramref name="preferredOffset"/>.
        /// Returns -1 when no match is found.
        /// </summary>
        internal static int FindBestMatchIndex(string text, string snippet, int preferredOffset)
        {
            int bestIndex    = -1;
            int bestDistance = int.MaxValue;
            int searchFrom   = 0;

            while (searchFrom < text.Length)
            {
                int idx = text.IndexOf(snippet, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                int distance = Math.Abs(idx - preferredOffset);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex    = idx;
                }

                searchFrom = idx + 1;
            }

            return bestIndex;
        }
    }
}
