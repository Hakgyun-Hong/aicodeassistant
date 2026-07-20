using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Shell;

namespace AIAssistant
{
    /// <summary>
    /// Collects build warnings from the VS Error List and extracts code context.
    /// </summary>
    public static class WarningCollector
    {
        /// <summary>
        /// Reads warnings from DTE.ToolWindows.ErrorList via the ErrorItems collection.
        /// Must be called on the UI thread.
        /// </summary>
        public static List<WarningTodoItem> CollectWarnings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var items = new List<WarningTodoItem>();

            try
            {
                var dte = Helper.DteInstance;
                if (dte == null) return items;

                var errorList = dte.ToolWindows.ErrorList;
                var errorItems = errorList.ErrorItems;

                for (int i = 1; i <= errorItems.Count; i++)
                {
                    try
                    {
                        var errorItem = errorItems.Item(i);

                        // Only warnings (not errors or messages).
                        // DTE ErrorList doesn't expose severity directly in older APIs,
                        // so filter by description text. C# warnings contain "warning"
                        // (e.g. "warning CS8602: Dereference of a possibly null reference").
                        // Items whose description doesn't contain "warning" are errors or
                        // informational messages and are skipped.
                        if (description.IndexOf("warning", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string filePath = errorItem.FileName ?? "";
                        int line = errorItem.Line;
                        int column = errorItem.Column;
                        string description = errorItem.Description ?? "";
                        string project = errorItem.Project ?? "";

                        // Skip if no file path (build-level warnings)
                        if (string.IsNullOrWhiteSpace(filePath)) continue;

                        // Extract warning code from description (e.g. "CS8602: ...")
                        string code = ExtractWarningCode(description);

                        var todo = new WarningTodoItem
                        {
                            Code = code,
                            Message = description,
                            FilePath = filePath,
                            Line = line,
                            Column = column,
                            Project = project,
                            Status = WarningFixStatus.PendingAnalysis,
                            CreatedAt = DateTimeOffset.Now
                        };

                        // Compute file hash for stale detection
                        if (File.Exists(filePath))
                        {
                            todo.OriginalFileHash = ComputeSha256(filePath);
                        }

                        items.Add(todo);
                    }
                    catch
                    {
                        // Skip individual item errors
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIAssistant] WarningCollector error: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// Reads code context around the warning line (±contextLines).
        /// </summary>
        public static string ReadCodeContext(string filePath, int line, int contextLines = 50)
        {
            if (!File.Exists(filePath)) return "";

            try
            {
                var lines = File.ReadAllLines(filePath);
                int warningIndex = Math.Max(line - 1, 0);
                int start = Math.Max(0, warningIndex - contextLines);
                int end = Math.Min(lines.Length - 1, warningIndex + contextLines);

                var sb = new StringBuilder();
                for (int i = start; i <= end; i++)
                {
                    sb.AppendLine(lines[i]);
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Extracts warning code like "CS8602" from a description string.
        /// </summary>
        private static string ExtractWarningCode(string description)
        {
            if (string.IsNullOrEmpty(description)) return "";

            // Match patterns like CS8602, CA1000, IDE0001, C4996 (C++), TS2322 (TypeScript), MSB3270
            // General format: 1 to 4 uppercase letters followed by 1 to 5 digits.
            var match = System.Text.RegularExpressions.Regex.Match(
                description, @"\b([A-Z]{1,4}\d{1,5})\b");
            return match.Success ? match.Value : "";
        }

        /// <summary>
        /// Computes SHA-256 hash of a file for stale detection.
        /// </summary>
        public static string ComputeSha256(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash)
                        sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
