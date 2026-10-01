using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIAssistant
{
    /// <summary>
    /// Parses Samsung SAM quality report HTML files.
    /// Supports loading from local file path OR HTTP/HTTPS URL.
    /// Uses Regex-based parsing (no external NuGet, .NET 4.7.2 compatible).
    ///
    /// SAM HTML structure:
    ///   &lt;a name="NVcc"&gt;  ...  &lt;table class="details"&gt; ... &lt;/table&gt;
    ///
    /// Each &lt;a name="NVxx"&gt; anchor identifies one metric section.
    /// Anchor → metric abbreviation mapping is defined in <see cref="AnchorToMetric"/>.
    /// </summary>
    public static class SamReportParser
    {
        // ─── Anchor name → (MetricAbbr, MetricFullName) ────────────────────
        private static readonly Dictionary<string, (string Abbr, string Full)> AnchorToMetric
            = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["NVcc"]   = ("CC",   "CyclomaticComplexity"),
            ["NVdc"]   = ("DC",   "DuplicateCode"),
            ["NVmcd"]  = ("MCD",  "ModuleCircularDependency"),
            ["NVcbo"]  = ("CBO",  "CouplingBetweenObjects"),
            ["NVloc"]  = ("LOC",  "LinesOfCode"),
            ["NVgv"]   = ("GV",   "GlobalVariable"),
            ["NVpp"]   = ("PP",   "PredefinedPreprocessor"),
            ["NVncs"]  = ("NCS",  "NestedControlStatements"),
            ["NVdepc"] = ("DepC", "DependencyComplexity"),
            ["NVgm"]   = ("GM",   "GodModule"),
        };

        // ─── Column header → field setter dispatch table ────────────────────
        // Built per metric in ParseTableRows so we don't hard-code column order.

        // ─── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Load and parse a SAM HTML report from a local file path.
        /// </summary>
        public static List<SamTodoItem> ParseFile(string filePath)
        {
            string html = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseHtml(html);
        }

        /// <summary>
        /// Load and parse a SAM HTML report from an HTTP/HTTPS URL.
        /// </summary>
        public static List<SamTodoItem> ParseUrl(string url, int timeoutSeconds = 30)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutSeconds * 1000;
            request.UserAgent = "SAMFixAssistant/1.0";

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string html = reader.ReadToEnd();
                return ParseHtml(html);
            }
        }

        /// <summary>
        /// Parse SAM HTML content string and return all metric violation items.
        /// </summary>
        public static List<SamTodoItem> ParseHtml(string html)
        {
            var results = new List<SamTodoItem>();
            if (string.IsNullOrWhiteSpace(html)) return results;

            // Step 1: find all <a name="NVxx"> anchor positions in document order.
            // Pattern: <a name="NVcc"> or <a name='NVcc'> (case-insensitive)
            var anchorPattern = new Regex(
                @"<a\s+name\s*=\s*[""']?(\w+)[""']?\s*/?>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var anchors = new List<(string Name, int Index)>();
            foreach (Match m in anchorPattern.Matches(html))
            {
                string aName = m.Groups[1].Value;
                if (AnchorToMetric.ContainsKey(aName))
                    anchors.Add((aName, m.Index));
            }

            // Step 2: for each anchor, extract the next <table class="details"> section.
            for (int i = 0; i < anchors.Count; i++)
            {
                var (anchorName, anchorIdx) = anchors[i];
                int sectionEnd = (i + 1 < anchors.Count) ? anchors[i + 1].Index : html.Length;
                string section = html.Substring(anchorIdx, sectionEnd - anchorIdx);

                var (abbr, full) = AnchorToMetric[anchorName];

                var items = ParseSection(section, abbr, full);
                results.AddRange(items);

                Log($"[{abbr}] Parsed {items.Count} row(s) from section '{anchorName}'.");
            }

            return results;
        }

        // ─── Private helpers ────────────────────────────────────────────────

        private static List<SamTodoItem> ParseSection(string section, string metricAbbr, string metricFull)
        {
            var items = new List<SamTodoItem>();

            // Find the details table.
            int tableStart = section.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
            if (tableStart < 0) return items;

            int tableEnd = section.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
            if (tableEnd < 0) tableEnd = section.Length;

            string table = section.Substring(tableStart, tableEnd - tableStart + 8);

            // Extract all <tr> blocks.
            var trPattern = new Regex(@"<tr\b[^>]*>(.*?)</tr>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

            var rows = new List<List<string>>();
            foreach (Match trMatch in trPattern.Matches(table))
            {
                string rowHtml = trMatch.Groups[1].Value;
                var cells = ExtractCells(rowHtml);
                if (cells.Count > 0)
                    rows.Add(cells);
            }

            if (rows.Count < 2) return items;  // need at least header + 1 data row

            // First row that has <th> is the header.
            List<string> headers = null;
            int dataStartIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                // Check original row HTML for <th> tags
                if (table.Contains("<th"))
                {
                    // Re-extract headers specifically from <th> tags
                    if (headers == null)
                    {
                        var thPattern = new Regex(@"<th\b[^>]*>(.*?)</th>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        var thMatches = thPattern.Matches(table);
                        if (thMatches.Count > 0)
                        {
                            headers = new List<string>();
                            foreach (Match th in thMatches)
                                headers.Add(StripHtml(th.Groups[1].Value).Trim());
                            dataStartIdx = 0; // data rows still start at rows[0] since we extracted headers separately
                        }
                        break;
                    }
                }
            }

            if (headers == null || headers.Count == 0) return items;

            // Parse data rows (skip header rows).
            // Identify data rows: rows whose first cell is numeric (row No.)
            int rowNo = 0;
            foreach (var cells in rows)
            {
                if (cells.Count == 0) continue;
                // Skip if first cell isn't a number (likely another header row)
                if (!int.TryParse(cells[0].Trim(), out int parsedNo)) continue;

                rowNo++;
                var item = new SamTodoItem
                {
                    Metric         = metricAbbr,
                    MetricFullName = metricFull,
                    RowNo          = parsedNo,
                    Status         = SamFixStatus.PendingAnalysis
                };

                // Map cells to fields by header column index.
                for (int c = 0; c < Math.Min(headers.Count, cells.Count); c++)
                {
                    string header = headers[c].ToUpperInvariant().Replace(" ", "").Replace(".", "");
                    string val    = cells[c].Trim();
                    MapCell(item, header, val, metricAbbr);
                }

                // Determine if this metric has a code location.
                if (!item.HasCodeLocation)
                    item.Status = SamFixStatus.ArchitectureOnly;

                items.Add(item);
            }

            return items;
        }

        /// <summary>Map a single cell value to the appropriate SamTodoItem field by column header name.</summary>
        private static void MapCell(SamTodoItem item, string header, string val, string metric)
        {
            switch (header)
            {
                case "NO":              /* skip row index */          break;
                case "METRIC":          item.Metric = val;            break;
                case "PATH":            item.RelativePath = val;      break;
                case "ENTITY":          item.Entity = val;            break;
                case "VALUE":           TrySetInt(val, v => item.Value = v); break;
                case "LINENO":          TrySetInt(val, v => item.LineNo = v); break;
                case "LINESTART":       TrySetInt(val, v => item.LineNo = v); break;
                case "LINEEND":         TrySetInt(val, v => item.LineEnd = v); break;
                case "LINECOUNT":       TrySetInt(val, v => item.LineCount = v); break;
                case "TOKEN":           TrySetInt(val, v => item.Token = v); break;
                case "VIOLATION":       item.Violation = val;         break;
                case "BLOCKKEY":        item.BlockKey = val;          break;
                case "FROMMODULE":      item.FromModule = val;        break;
                case "TOMODULE":        item.ToModule = val;          break;
                case "RELATIONCOUNT":   TrySetInt(val, v => item.RelationCount = v); break;
                case "MARK":            item.Mark = val;              break;
                case "KEY":             item.Key = val;               break;
                case "PACKAGE":         item.Package = val;           break;
                case "CLASS":           item.Class = val;             break;
                case "CLASSTYPE":       item.ClassType = val;         break;
                case "ACCESSINGFUNC":   item.AccessingFunc = val;     break;
                case "NAME":            item.Name = val;              break;
                case "MACROTYPE":       item.MacroType = val;         break;
                case "CODELOC":         TrySetInt(val, v => item.CodeLOC = v); break;
                case "MACROCOUNT":      TrySetInt(val, v => item.MacroCount = v); break;
                case "PP_LOC":          TrySetInt(val, v => item.PP_LOC = v); break;
                case "SIGNATURE":       item.Signature = val;         break;
                case "PARENTMODULE":    item.ParentModule = val;      break;
                case "COMPLEXITY":      TrySetInt(val, v => item.Complexity = v); break;
                case "MODULEMAXFANOUT": TrySetInt(val, v => item.ModuleMaxFanOut = v); break;
                case "MODULE":          item.Module = val;            break;
                case "BLOC":            TrySetInt(val, v => item.BLOC = v); break;
                case "FUNCTIONCNT":     TrySetInt(val, v => item.FunctionCnt = v); break;
                case "BLOCXFUNCTIONCNT":TrySetInt(val, v => item.BLOCxFunctionCnt = v); break;
                case "MODULEPATH":      item.ModulePath = val;        break;
            }
        }

        /// <summary>Extract cell text values from a single &lt;tr&gt; inner HTML.</summary>
        private static List<string> ExtractCells(string rowHtml)
        {
            var cells = new List<string>();
            // Match both <td ...> and <th ...>
            var cellPattern = new Regex(@"<t[dh]\b[^>]*>(.*?)</t[dh]>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
            foreach (Match m in cellPattern.Matches(rowHtml))
                cells.Add(StripHtml(m.Groups[1].Value));
            return cells;
        }

        /// <summary>Remove all HTML tags and decode common entities.</summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string text = Regex.Replace(html, @"<[^>]+>", "");
            text = text.Replace("&amp;", "&")
                       .Replace("&lt;",  "<")
                       .Replace("&gt;",  ">")
                       .Replace("&nbsp;", " ")
                       .Replace("&#160;", " ");
            return text.Trim();
        }

        private static void TrySetInt(string val, Action<int> setter)
        {
            if (int.TryParse(val, out int n)) setter(n);
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][SamParser] {msg}");
    }
}
