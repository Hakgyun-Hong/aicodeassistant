using System;
using System.IO;
using System.Text;

namespace AIAssistant
{
    /// <summary>
    /// Applies an accepted warning fix to the source file.
    /// Uses a 3-tier matching strategy to handle line shifts and minor whitespace changes.
    /// All steps emit [AIAssistant][Apply] diagnostics to the VS Debug Output window.
    /// </summary>
    public static class WarningFixApplier
    {
        // ─── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Apply the fix: hash check (advisory) → 3-tier snippet search → backup → replace.
        /// Returns null on success, or an error message string on failure.
        /// </summary>
        public static string Apply(WarningTodoItem item)
        {
            if (item == null) return "No item selected.";

            Log($"[{item.Code}] Apply started. File={item.ShortFilePath}, Line={item.Line}");

            if (string.IsNullOrWhiteSpace(item.FilePath))
                return Fail(item, "File path is empty.");

            if (!File.Exists(item.FilePath))
                return Fail(item, $"File not found: {item.FilePath}");

            if (string.IsNullOrWhiteSpace(item.OriginalSnippet))
                return Fail(item, "Original snippet is empty — cannot determine what to replace.");

            if (string.IsNullOrWhiteSpace(item.ProposedSnippet))
                return Fail(item, "Proposed snippet is empty — nothing to apply.");

            try
            {
                // ── Step 1: Hash check (advisory only — does NOT block apply) ──
                // A hash mismatch means the file changed since analysis.
                // We still attempt to apply; the snippet search decides the final outcome.
                if (!string.IsNullOrWhiteSpace(item.OriginalFileHash))
                {
                    string currentHash = WarningCollector.ComputeSha256(item.FilePath);
                    bool hashMatch = string.Equals(currentHash, item.OriginalFileHash,
                                                   StringComparison.OrdinalIgnoreCase);
                    Log($"[{item.Code}] Step 1 Hash: {(hashMatch ? "MATCH" : "CHANGED (advisory — continuing apply)")}");
                }
                else
                {
                    Log($"[{item.Code}] Step 1 Hash: skipped (no stored hash).");
                }

                // ── Step 2: Read file & 3-tier snippet matching ───────────────
                string currentText    = File.ReadAllText(item.FilePath, Encoding.UTF8);
                string normalizedText = currentText.Replace("\r\n", "\n");
                string rawSnippet     = item.OriginalSnippet.Replace("\r\n", "\n").Trim();

                int preferredOffset = WarningFixApplierHelpers.GetLineStartOffset(normalizedText, item.Line);
                Log($"[{item.Code}] Step 2 Searching. PreferredOffset={preferredOffset} (line {item.Line}). Snippet={rawSnippet.Length} chars.");
                Log($"[{item.Code}] Snippet preview: {Truncate(rawSnippet, 120)}");

                // ── 3-tier matching (also callable standalone via FindSnippet) ──
                var match = FindSnippet(normalizedText, rawSnippet, preferredOffset, item.Code);
                if (!match.Found)
                {
                    Log($"[{item.Code}] All tiers FAILED — snippet not found in file.");
                    item.Status    = WarningFixStatus.Stale;
                    item.UpdatedAt = DateTimeOffset.Now;
                    return "Original code snippet was not found (tried exact, whitespace-normalized, and anchor-line matching). " +
                           "The file may have been heavily edited. Use \uD83D\uDD04 Re-analyze to get a fresh suggestion.";
                }

                Log($"[{item.Code}] Match accepted via {match.Tier}. charIndex={match.Index}, length={match.Length}.");

                // ── Step 3: Backup ────────────────────────────────────────────
                string backupPath = item.FilePath + ".warningfix.bak";
                File.Copy(item.FilePath, backupPath, overwrite: true);
                Log($"[{item.Code}] Step 3 Backup: {System.IO.Path.GetFileName(backupPath)}");

                // ── Step 4: Replace ───────────────────────────────────────────
                string proposedSnippet = item.ProposedSnippet.Replace("\r\n", "\n");
                string newText = normalizedText.Substring(0, match.Index)
                               + proposedSnippet
                               + normalizedText.Substring(match.Index + match.Length);

                // Restore CRLF if the original file used it
                if (currentText.Contains("\r\n"))
                    newText = newText.Replace("\n", "\r\n");

                File.WriteAllText(item.FilePath, newText, Encoding.UTF8);
                Log($"[{item.Code}] Step 4 File written. {currentText.Length} → {newText.Length} chars.");

                // ── Step 5: Update status ─────────────────────────────────────
                item.Status    = WarningFixStatus.Accepted;
                item.UpdatedAt = DateTimeOffset.Now;
                Log($"[{item.Code}] Apply SUCCESS via {match.Tier}.");

                return null; // success
            }
            catch (Exception ex)
            {
                Log($"[{item.Code}] Apply EXCEPTION: {ex.Message}");
                item.Status        = WarningFixStatus.Failed;
                item.FailureReason = ex.Message;
                item.UpdatedAt     = DateTimeOffset.Now;
                return $"Apply failed: {ex.Message}";
            }
        }

        // ─── Private helpers ─────────────────────────────────────────────────

        private static string Fail(WarningTodoItem item, string reason)
        {
            Log($"[{item?.Code}] Precondition FAILED: {reason}");
            if (item != null)
            {
                item.Status        = WarningFixStatus.Failed;
                item.FailureReason = reason;
                item.UpdatedAt     = DateTimeOffset.Now;
            }
            return reason;
        }

        /// <summary>Write a diagnostic message to the VS Debug Output window.</summary>
        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][Apply] {msg}");

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…";
        }

        // ─── Standalone testable snippet finder ──────────────────────────────

        /// <summary>
        /// Run the 3-tier snippet search against <paramref name="fileText"/> without
        /// touching the file system. Useful for unit tests and manual debugging.
        /// </summary>
        /// <param name="fileText">Full source file content (any line endings).</param>
        /// <param name="snippet">The <c>originalSnippet</c> from the LLM suggestion.</param>
        /// <param name="hintLine">1-based line number of the warning (used to prefer the closest match).</param>
        /// <param name="label">Optional label for Debug output (e.g. the warning code).</param>
        public static SnippetMatchResult FindSnippet(
            string fileText, string snippet, int hintLine, string label = "")
        {
            string normText    = fileText.Replace("\r\n", "\n");
            string normSnippet = snippet.Replace("\r\n", "\n").Trim();
            int    preferred   = WarningFixApplierHelpers.GetLineStartOffset(normText, hintLine);

            return FindSnippet(normText, normSnippet, preferred, label);
        }

        // Internal overload used by Apply() (already has normalized strings)
        internal static SnippetMatchResult FindSnippet(
            string normalizedText, string normalizedSnippet, int preferredOffset, string label)
        {
            // Tier 1
            int idx = WarningFixApplierHelpers.FindBestMatchIndex(
                normalizedText, normalizedSnippet, preferredOffset);
            if (idx >= 0)
            {
                Log($"[{label}] Tier 1 exact match at charIndex={idx}.");
                return new SnippetMatchResult(
                    true, "Tier 1 (exact)", idx, normalizedSnippet.Length,
                    normalizedText.Substring(idx, normalizedSnippet.Length));
            }

            // Tier 2
            Log($"[{label}] Tier 1 FAILED. Trying Tier 2 (whitespace-normalized)...");
            int wsIdx = WarningFixApplierHelpers.FindWhitespaceNormalizedMatch(
                normalizedText, normalizedSnippet, preferredOffset,
                out int realStart, out int realEnd);
            if (wsIdx >= 0)
            {
                int len = realEnd - realStart;
                Log($"[{label}] Tier 2 match at charIndex={realStart}, length={len}.");
                return new SnippetMatchResult(
                    true, "Tier 2 (whitespace-normalized)", realStart, len,
                    normalizedText.Substring(realStart, len));
            }

            // Tier 3
            Log($"[{label}] Tier 2 FAILED. Trying Tier 3 (anchor lines)...");
            var anchor = WarningFixApplierHelpers.FindByAnchorLines(
                normalizedText, normalizedSnippet, preferredOffset);
            if (anchor.HasValue)
            {
                int len = anchor.Value.end - anchor.Value.start;
                Log($"[{label}] Tier 3 match at charIndex={anchor.Value.start}, length={len}.");
                return new SnippetMatchResult(
                    true, "Tier 3 (anchor lines)", anchor.Value.start, len,
                    normalizedText.Substring(anchor.Value.start, len));
            }

            Log($"[{label}] All tiers FAILED.");
            return SnippetMatchResult.NotFound;
        }
    }

    /// <summary>Result returned by <see cref="WarningFixApplier.FindSnippet"/>.</summary>
    public sealed class SnippetMatchResult
    {
        public static readonly SnippetMatchResult NotFound =
            new SnippetMatchResult(false, "", -1, 0, "");

        public bool   Found         { get; }
        public string Tier          { get; }  // e.g. "Tier 1 (exact)"
        public int    Index         { get; }  // char offset in normalized text
        public int    Length        { get; }  // length of matched block
        public string MatchedText   { get; }  // the actual matched block

        public SnippetMatchResult(bool found, string tier, int index, int length, string matchedText)
        {
            Found       = found;
            Tier        = tier;
            Index       = index;
            Length      = length;
            MatchedText = matchedText;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WarningFixApplierHelpers — now public for unit-test access
    // ─────────────────────────────────────────────────────────────────────────

    public static class WarningFixApplierHelpers
    {
        // ── Line ↔ character-offset utilities ────────────────────────────────

        /// <summary>Character offset where 1-based <paramref name="targetLine"/> begins.</summary>
        public static int GetLineStartOffset(string text, int targetLine)
        {
            if (targetLine <= 1) return 0;
            int lineNumber = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lineNumber++;
                    if (lineNumber >= targetLine) return i + 1;
                }
            }
            return text.Length;
        }

        /// <summary>Character offset just past the end of 1-based <paramref name="targetLine"/>.</summary>
        public static int GetLineEndOffset(string text, int targetLine)
        {
            int start = GetLineStartOffset(text, targetLine);
            for (int i = start; i < text.Length; i++)
                if (text[i] == '\n') return i + 1;
            return text.Length;
        }

        /// <summary>1-based line number that contains character offset <paramref name="charOffset"/>.</summary>
        public static int GetLineAtOffset(string text, int charOffset)
        {
            int line = 1;
            int end  = Math.Min(charOffset, text.Length);
            for (int i = 0; i < end; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        // ── Tier 1 helper ─────────────────────────────────────────────────────

        /// <summary>
        /// Find all occurrences of <paramref name="snippet"/> in <paramref name="text"/>
        /// and return the start index of the occurrence closest to <paramref name="preferredOffset"/>.
        /// Returns -1 when not found.
        /// </summary>
        public static int FindBestMatchIndex(string text, string snippet, int preferredOffset)
        {
            int bestIndex    = -1;
            int bestDistance = int.MaxValue;
            int searchFrom   = 0;

            while (searchFrom < text.Length)
            {
                int idx = text.IndexOf(snippet, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                int distance = Math.Abs(idx - preferredOffset);
                if (distance < bestDistance) { bestDistance = distance; bestIndex = idx; }

                searchFrom = idx + 1;
            }

            return bestIndex;
        }

        // ── Tier 2: whitespace-normalized match ───────────────────────────────

        /// <summary>
        /// Collapse leading whitespace on each line to a canonical form so that
        /// indentation changes (tab↔space, reformatting) don't block matching.
        /// Populates <paramref name="realStart"/>/<paramref name="realEnd"/> with
        /// the character range in the ORIGINAL (un-normalized) text.
        /// Returns the match index in the normalized text, or -1.
        /// </summary>
        public static int FindWhitespaceNormalizedMatch(
            string text, string snippet,
            int preferredOffset,
            out int realStart, out int realEnd)
        {
            realStart = -1;
            realEnd   = -1;

            string normText    = NormalizeIndent(text);
            string normSnippet = NormalizeIndent(snippet);

            int idx = FindBestMatchIndex(normText, normSnippet, preferredOffset);
            if (idx < 0) return -1;

            // Map normalized index → 1-based line numbers → real text character range
            int startLine = GetLineAtOffset(normText, idx);
            int endLine   = GetLineAtOffset(normText, idx + normSnippet.Length - 1);

            realStart = GetLineStartOffset(text, startLine);
            realEnd   = GetLineEndOffset(text, endLine);

            return idx;
        }

        /// <summary>
        /// Normalize each line to a single leading space + trimmed content.
        /// This makes indentation changes invisible to string matching.
        /// </summary>
        public static string NormalizeIndent(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var line in text.Split('\n'))
            {
                sb.Append(' ');
                sb.Append(line.TrimStart());
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ── Tier 3: first+last line anchor ───────────────────────────────────

        /// <summary>
        /// Locate the block in <paramref name="text"/> whose first and last
        /// non-trivial lines contain the same trimmed content as those of
        /// <paramref name="snippet"/>. Useful when the LLM included/excluded a few
        /// surrounding context lines that differ from the real file.
        /// Returns the (start, end) character range, or null.
        /// </summary>
        public static (int start, int end)? FindByAnchorLines(
            string text, string snippet, int preferredOffset)
        {
            string[] sLines = snippet.Split('\n');

            // Pick anchor: first and last non-trivial lines (length > 4 after trim)
            string firstAnchor = null, lastAnchor = null;
            for (int i = 0; i < sLines.Length && firstAnchor == null; i++)
            {
                string t = sLines[i].Trim();
                if (t.Length > 4) firstAnchor = t;
            }
            for (int i = sLines.Length - 1; i >= 0 && lastAnchor == null; i--)
            {
                string t = sLines[i].Trim();
                if (t.Length > 4) lastAnchor = t;
            }

            if (firstAnchor == null || lastAnchor == null || firstAnchor == lastAnchor)
                return null;

            string[] tLines    = text.Split('\n');
            int      charOff   = 0;
            int      bestScore = int.MaxValue;
            int      bestStart = -1, bestEnd = -1;

            for (int i = 0; i < tLines.Length; i++)
            {
                if (tLines[i].Trim().Contains(firstAnchor))
                {
                    int maxSearch = Math.Min(tLines.Length - 1, i + sLines.Length + 5);
                    for (int j = i; j <= maxSearch; j++)
                    {
                        if (tLines[j].Trim().Contains(lastAnchor))
                        {
                            int blockStart = charOff;
                            int blockEnd   = GetLineEndOffset(text, j + 1); // 1-based
                            int distance   = Math.Abs(blockStart - preferredOffset);
                            if (distance < bestScore)
                            {
                                bestScore = distance;
                                bestStart = blockStart;
                                bestEnd   = blockEnd;
                            }
                            break;
                        }
                    }
                }
                charOff += tLines[i].Length + 1; // +1 for the '\n'
            }

            if (bestStart < 0) return null;
            return (bestStart, bestEnd);
        }
    }
}
