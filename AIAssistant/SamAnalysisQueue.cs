using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIAssistant
{
    /// <summary>
    /// Background queue that sends SAM metric items to LLM for analysis.
    /// Each metric type generates a tailored prompt so the LLM understands
    /// the specific quality issue and proposes appropriate code fixes.
    /// </summary>
    public sealed class SamAnalysisQueue
    {
        private readonly ConcurrentQueue<SamTodoItem> _queue = new ConcurrentQueue<SamTodoItem>();
        private readonly SemaphoreSlim _parallelism;
        private readonly SamTodoStore _store;
        private CancellationTokenSource _cts;
        private int _totalEnqueued;
        private int _totalProcessed;

        public event Action<int, int> ProgressChanged;

        public SamAnalysisQueue(SamTodoStore store, int maxParallel = 3)
        {
            _store       = store;
            _parallelism = new SemaphoreSlim(maxParallel, maxParallel);
        }

        public void Enqueue(SamTodoItem item)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _totalEnqueued);
        }

        /// <summary>Signal cancellation and drain the queue.</summary>
        public void Cancel()
        {
            _cts?.Cancel();
            while (_queue.TryDequeue(out _)) { }
        }

        public void ResetAndEnqueue(IEnumerable<SamTodoItem> items)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (item == null) continue;
                item.LlmSummary      = null;
                item.LlmExplanation  = null;
                item.OriginalSnippet = null;
                item.ProposedSnippet = null;
                item.UnifiedDiff     = null;
                item.RiskLevel       = null;
                item.FailureReason   = null;
                item.Status          = SamFixStatus.PendingAnalysis;
                item.UpdatedAt       = DateTimeOffset.Now;
                Enqueue(item);
            }
            _store.NotifyChanged();
        }

        public async Task ReanalyzeAsync(SamTodoItem item)
        {
            if (item == null) return;
            item.LlmSummary      = null;
            item.LlmExplanation  = null;
            item.OriginalSnippet = null;
            item.ProposedSnippet = null;
            item.UnifiedDiff     = null;
            item.RiskLevel       = null;
            item.FailureReason   = null;
            item.Status          = SamFixStatus.PendingAnalysis;
            item.UpdatedAt       = DateTimeOffset.Now;
            _store.NotifyChanged();

            Interlocked.Increment(ref _totalEnqueued);

            if (_cts == null || _cts.IsCancellationRequested)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            await _parallelism.WaitAsync(_cts.Token);
            await ProcessOneAsync(item, _cts.Token);
        }

        public async Task ProcessAllAsync()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _totalProcessed = 0;

            var tasks = new ConcurrentBag<Task>();

            while (_queue.TryDequeue(out var item))
            {
                if (_cts.IsCancellationRequested) break;
                await _parallelism.WaitAsync(_cts.Token);
                tasks.Add(ProcessOneAsync(item, _cts.Token));
            }

            try   { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
        }

        // ─── Metric category ─────────────────────────────────────────────────

        /// <summary>True for metrics where a concrete code snippet fix or decomposition fix is possible.</summary>
        private static bool IsCodeLevelMetric(string metric)
        {
            switch (metric)
            {
                case "CC": case "NCS": case "GV": case "PP": case "DC": case "LOC":
                    return true;
                default: // GM, MCD, CBO, DepC → architecture guidance
                    return false;
            }
        }

        // ─── Core processing ────────────────────────────────────────────────

        private async Task ProcessOneAsync(SamTodoItem item, CancellationToken ct)
        {
            try
            {
                item.Status    = SamFixStatus.Analyzing;
                item.UpdatedAt = DateTimeOffset.Now;
                _store.NotifyChanged();

                // Read code context — targeted by metric type
                string codeContext = null;
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    if (item.Metric == "LOC")
                    {
                        codeContext = ReadLocContext(item.FilePath, item.LineNo);
                    }
                    else if (IsCodeLevelMetric(item.Metric))
                    {
                        // For CC/NCS: read wide function body (up to 200 lines)
                        // For GV/PP/DC: read 100 lines
                        int radius = (item.Metric == "CC" || item.Metric == "NCS") ? 100 : 50;
                        int contextLine = item.LineNo > 0 ? item.LineNo : 1;
                        codeContext = ReadExtendedContext(item.FilePath, contextLine, radius);
                    }
                    else
                    {
                        // Architecture metrics: read file structure
                        codeContext = ReadFileStructure(item.FilePath);
                    }
                }

                if (string.IsNullOrEmpty(codeContext))
                    Log($"[{item.Metric}] No code context on disk for {item.RelativePath}:{item.LineNo} (resolved path: '{item.FilePath}')");

                string systemPrompt = BuildSystemPrompt(item);
                string userContent  = BuildUserContent(item, codeContext);

                ct.ThrowIfCancellationRequested();

                string response = await LlmClient.AskAsync(systemPrompt, userContent);
                Log($"[{item.Metric}] Response: {response?.Length ?? 0} chars.");
                Log($"[{item.Metric}] Preview: {Truncate(response, 300)}");

                ct.ThrowIfCancellationRequested();

                ParseLlmResponse(item, response);

                if (item.Status != SamFixStatus.Failed && item.Status != SamFixStatus.ArchitectureOnly)
                    item.Status = SamFixStatus.Suggested;

                item.UpdatedAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                item.Status        = SamFixStatus.Failed;
                item.FailureReason = "Cancelled.";
            }
            catch (Exception ex)
            {
                item.Status        = SamFixStatus.Failed;
                item.FailureReason = ex.Message;
                item.UpdatedAt     = DateTimeOffset.Now;
            }
            finally
            {
                _parallelism.Release();
                Interlocked.Increment(ref _totalProcessed);
                ProgressChanged?.Invoke(_totalProcessed, _totalEnqueued);
                _store.NotifyChanged();
            }
        }

        // ─── Extended context readers ───────────────────────────────────────

        /// <summary>Read wider code context around the target line.</summary>
        private static string ReadExtendedContext(string filePath, int centerLine, int radius)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                int start = Math.Max(0, centerLine - radius - 1);
                int end   = Math.Min(lines.Length, centerLine + radius);
                var sb = new StringBuilder();
                for (int i = start; i < end; i++)
                    sb.AppendLine($"{i + 1,5}: {lines[i]}");
                return sb.ToString();
            }
            catch { return WarningCollector.ReadCodeContext(filePath, centerLine); }
        }

        /// <summary>
        /// Reads LOC context: includes file outline and a candidate extraction block
        /// so the LLM has actual code to extract into a new file.
        /// </summary>
        private static string ReadLocContext(string filePath, int targetLine)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                var sb = new StringBuilder();
                sb.AppendLine($"File: {Path.GetFileName(filePath)}  ({lines.Length} lines total)");
                sb.AppendLine();

                // 1. File outline
                sb.AppendLine("=== FILE OUTLINE ===");
                sb.AppendLine(ReadFileStructure(filePath));
                sb.AppendLine();

                // 2. Candidate lines for extraction
                sb.AppendLine("=== CODE CONTENT (Candidate area for extraction) ===");
                int start, end;
                if (targetLine > 0 && targetLine <= lines.Length)
                {
                    start = Math.Max(0, targetLine - 30);
                    end   = Math.Min(lines.Length, targetLine + 180);
                }
                else
                {
                    start = 0;
                    end   = Math.Min(lines.Length, 250);
                }

                for (int i = start; i < end; i++)
                    sb.AppendLine($"{i + 1,5}: {lines[i]}");

                return sb.ToString();
            }
            catch
            {
                return ReadExtendedContext(filePath, Math.Max(1, targetLine), 100);
            }
        }

        /// <summary>
        /// Read file structure for architecture metrics: function signatures with line numbers.
        /// Gives LLM enough info to suggest specific decomposition points.
        /// </summary>
        private static string ReadFileStructure(string filePath)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                var sb = new StringBuilder();
                sb.AppendLine($"File: {Path.GetFileName(filePath)}  ({lines.Length} lines total)");
                sb.AppendLine();

                // Extract function/method signatures
                var funcPattern = new Regex(
                    @"^\s*(public|private|protected|internal|static|virtual|override|async|void|int|bool|string|double|float|char|long|byte|short|uint|ulong|Task|var|auto|inline|explicit|extern|unsigned|signed|const)\b.+[({]",
                    RegexOptions.Compiled);

                var classPattern = new Regex(
                    @"^\s*(public|private|protected|internal|sealed|abstract|static)?\s*(partial\s+)?(class|struct|interface|enum|namespace)\s+",
                    RegexOptions.Compiled);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (classPattern.IsMatch(line))
                    {
                        sb.AppendLine($"L{i + 1,5}: {line.Trim()}");
                    }
                    else if (funcPattern.IsMatch(line) && !line.TrimStart().StartsWith("//"))
                    {
                        int bodyLen = CountFunctionLength(lines, i);
                        sb.AppendLine($"L{i + 1,5}: {line.Trim()}  // {bodyLen} lines");
                    }
                }

                // If file is short enough, include full content
                if (lines.Length <= 60)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== FULL FILE ===");
                    for (int i = 0; i < lines.Length; i++)
                        sb.AppendLine($"{i + 1,5}: {lines[i]}");
                }

                return sb.ToString();
            }
            catch { return null; }
        }

        private static int CountFunctionLength(string[] lines, int startLine)
        {
            int depth = 0; bool started = false;
            for (int i = startLine; i < lines.Length && i < startLine + 500; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') { depth++; started = true; }
                    if (c == '}') depth--;
                }
                if (started && depth <= 0) return i - startLine + 1;
            }
            return lines.Length - startLine;
        }

        private static string GetSuggestedExtractedName(SamTodoItem item)
        {
            string baseName = string.IsNullOrEmpty(item.FilePath)
                ? (string.IsNullOrEmpty(item.RelativePath) ? "component" : Path.GetFileNameWithoutExtension(item.RelativePath))
                : Path.GetFileNameWithoutExtension(item.FilePath);

            string ext = string.IsNullOrEmpty(item.FilePath)
                ? (string.IsNullOrEmpty(item.RelativePath) ? ".h" : Path.GetExtension(item.RelativePath))
                : Path.GetExtension(item.FilePath);

            if (string.IsNullOrEmpty(ext)) ext = ".h";

            if (!string.IsNullOrEmpty(item.Entity))
            {
                string safe = Regex.Replace(item.Entity, @"[^\w]", "_").ToLowerInvariant();
                return $"{baseName}_{safe}{ext}";
            }

            if (item.Metric == "LOC")
            {
                if (ext.Equals(".h", StringComparison.OrdinalIgnoreCase) || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase))
                    return $"{baseName}_types{ext}";
                return $"{baseName}_helpers{ext}";
            }

            return $"{baseName}_extracted{ext}";
        }

        // ─── Prompt Construction ────────────────────────────────────────────

        private static string BuildSystemPrompt(SamTodoItem item)
        {
            bool codeLevel = IsCodeLevelMetric(item.Metric);
            string guidance = GetMetricGuidance(item.Metric);
            string suggestedNewFile = GetSuggestedExtractedName(item);

            if (codeLevel)
            {
                return
$@"You are a Principal Software Engineer specializing in C/C++/C# refactoring for Samsung SAM quality metrics.
METRIC TO FIX: {guidance}

CRITICAL RULES:
1. ACTIONABLE CODE ONLY:
   - NEVER output cop-outs like:
     * 'Local fix not possible'
     * 'No specific code snippet is provided'
     * 'To meet the SAM metric, decompose into smaller units' without giving the code.
2. REFACTORING STRATEGIES:
   - For LOC (LinesOfCode):
     * Identify a large struct, enum cluster, helper class, or group of functions in Code Context.
     * Propose extracting it to a new file (e.g. '{suggestedNewFile}').
     * originalSnippet: The EXACT character-for-character lines in Code Context to be removed.
     * proposedSnippet: '#include ""{suggestedNewFile}""' or clean replacement in the current file.
     * extractedFileName: '{suggestedNewFile}'
     * extractedFileContent: The complete compilable code for the new file (including #pragma once, necessary headers, and the extracted definitions).
     * canFix: true
   - For CC (CyclomaticComplexity) & NCS (NestedControlStatements):
     * Flatten branching with guard clauses (early returns).
     * Extract cohesive logic into named helper functions.
     * originalSnippet: The EXACT character-for-character function body from Code Context.
     * proposedSnippet: The complete refactored function body AND any extracted helper functions.
     * canFix: true
   - For GV (GlobalVariable):
     * Encapsulate into a class/struct or convert to parameter/singleton accessor.
     * canFix: true
   - For PP (PredefinedPreprocessor):
     * Replace macros with constexpr, enum class, or inline functions.
     * canFix: true
   - For DC (DuplicateCode):
     * Extract shared function and replace duplicate blocks.
     * canFix: true
3. SNIPPET ACCURACY:
   - originalSnippet MUST be a verbatim substring of Code Context (matching spaces and newlines).
   - proposedSnippet MUST compile and preserve existing semantics.

Return ONLY valid JSON (no markdown fences):
{{
  ""canFix"": true,
  ""summary"": ""Concise 1-line summary of fix"",
  ""explanation"": ""Detailed rationale (lines saved, CC reduced, design)"",
  ""originalSnippet"": ""exact lines from Code Context to replace"",
  ""proposedSnippet"": ""replacement lines in current file"",
  ""extractedFileName"": ""optional new file name (or empty)"",
  ""extractedFileContent"": ""optional full code for new file (or empty)"",
  ""unifiedDiff"": ""unified diff showing changes"",
  ""riskLevel"": ""Low|Medium|High""
}}
All newlines in strings must be \n, backslashes \\, quotes \"""".";
            }
            else
            {
                // Architecture-level metrics (GM, MCD, CBO, DepC)
                return
$@"You are a Principal Software Architect helping refactor module architectures for Samsung SAM quality compliance.
METRIC: {guidance}

CRITICAL RULES:
1. Provide a CONCRETE, DETAILED refactoring plan with REAL CODE BLOCKS.
2. NEVER output vague advice like 'Refactor into smaller components' or 'No specific code snippet is provided'.
3. Always include:
   - Exact proposed new file names (e.g. '{suggestedNewFile}').
   - Concrete code blocks showing the new classes, interfaces, or functions.
   - Exact #include modifications (which includes to remove, which to add).
   - Step-by-step developer instructions.
4. If a localized change can be safely applied right now (e.g. removing an unused include or forward declaration), provide originalSnippet & proposedSnippet with canFix: true!

Return ONLY valid JSON:
{{
  ""canFix"": false,
  ""summary"": ""one-line plan summary"",
  ""explanation"": ""DETAILED step-by-step refactoring plan with CONCRETE code blocks and file designs"",
  ""originalSnippet"": """",
  ""proposedSnippet"": """",
  ""extractedFileName"": ""suggested new file name if any"",
  ""extractedFileContent"": ""suggested extracted code if any"",
  ""unifiedDiff"": """",
  ""riskLevel"": ""Medium|High""
}}
All newlines in strings must be \n.";
            }
        }

        private static string GetMetricGuidance(string metric)
        {
            switch (metric)
            {
                case "CC":
                    return "CyclomaticComplexity — Function has too many decision branches.\n" +
                           "Target: CC ≤ 10. Fix by: extract sub-functions, guard clauses, table-driven logic.\n" +
                           "IMPORTANT: provide actual extracted function code, not just 'extract this part'.";
                case "NCS":
                    return "NestedControlStatements — if/for/while nested too deeply (depth > 3).\n" +
                           "Fix: guard clauses (early return), extract inner body to named functions, invert conditions.\n" +
                           "Show the complete refactored function with extractions.";
                case "DC":
                    return "DuplicateCode — identical/near-identical code blocks in multiple locations.\n" +
                           "Fix: extract into a shared function that BOTH callers invoke.\n" +
                           "Show the new shared function AND both call sites.";
                case "GV":
                    return "GlobalVariable — global var accessed by multiple functions.\n" +
                           "Fix: encapsulate in class/struct, convert to function parameter, or use singleton accessor.\n" +
                           "Show the struct/class wrapper AND updated accessor calls.";
                case "PP":
                    return "PredefinedPreprocessor — overuse of #define macros.\n" +
                           "Fix: replace with constexpr, inline function, enum class, or typed constant.\n" +
                           "Show the replacement AND updated usage sites.";
                case "LOC":
                    return "LinesOfCode — file/class exceeds LOC threshold.\n" +
                           "Fix: extract cohesive structs, enums, helper classes, or functions into a new file.\n" +
                           "Provide the exact extracted file content and the #include replacement in the current file.";
                case "GM":
                    return "GodModule — module has too many functions and lines.\n" +
                           "Plan: identify responsibility clusters → split into smaller modules.\n" +
                           "Name each new module, show concrete class/function code to move, and show #include changes.";
                case "MCD":
                    return "ModuleCircularDependency — modules A↔B have cyclic #include.\n" +
                           "Plan: introduce interface/abstraction layer, forward declarations, or dependency inversion.\n" +
                           "Show WHICH functions cause the cycle and concrete code to break it.";
                case "CBO":
                    return "CouplingBetweenObjects — class references too many other classes.\n" +
                           "Plan: extract interfaces, apply DIP, use facade pattern.\n" +
                           "List specific dependencies to remove with replacement code.";
                case "DepC":
                    return "DependencyComplexity — too many fan-out dependencies.\n" +
                           "Plan: introduce Facade, consolidate related dependencies, split module.\n" +
                           "List specific includes/dependencies to consolidate with code.";
                default:
                    return $"SAM metric '{metric}' violation. Provide specific, actionable fix or refactoring plan.";
            }
        }

        private static string BuildUserContent(SamTodoItem item, string codeContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Metric: {item.Metric} ({item.MetricFullName})");
            sb.AppendLine($"File: {item.RelativePath}");
            if (item.LineNo > 0)
                sb.AppendLine($"Line: {item.LineDisplay}");
            if (!string.IsNullOrEmpty(item.Entity))
                sb.AppendLine($"Entity/Function: {item.Entity}");
            if (item.Value > 0)
                sb.AppendLine($"Metric Value: {item.Value}");

            // Type-specific context
            switch (item.Metric)
            {
                case "DC":
                    sb.AppendLine($"Duplicate BlockKey: {item.BlockKey}");
                    sb.AppendLine($"Line Range: {item.LineNo}–{item.LineEnd} ({item.LineCount} lines, {item.Token} tokens)");
                    break;
                case "MCD":
                    sb.AppendLine($"From Module: {item.FromModule}  →  To Module: {item.ToModule}");
                    sb.AppendLine($"Relation Count: {item.RelationCount}");
                    break;
                case "GV":
                    sb.AppendLine($"Accessing Functions: {item.AccessingFunc}");
                    break;
                case "PP":
                    sb.AppendLine($"Macro Name: {item.Name}, Type: {item.MacroType}");
                    sb.AppendLine($"Lines: {item.LineNo}–{item.LineEnd}, MacroCount: {item.MacroCount}");
                    break;
                case "NCS":
                    sb.AppendLine($"Signature: {item.Signature}");
                    break;
                case "GM":
                    sb.AppendLine($"Module: {item.Module}, BLOC: {item.BLOC}, FunctionCount: {item.FunctionCnt}");
                    break;
            }

            if (!string.IsNullOrEmpty(codeContext))
            {
                string ext = string.IsNullOrEmpty(item.FilePath) ? "" :
                    System.IO.Path.GetExtension(item.FilePath).TrimStart('.');
                sb.AppendLine();
                sb.AppendLine($"Code Context (relevant area near line {item.LineDisplay}):");
                sb.AppendLine($"```{ext}");
                sb.AppendLine(codeContext);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("(No source file available on disk — provide concrete code patterns and step-by-step refactoring.)");
            }

            return sb.ToString();
        }

        // ─── Response Parsing (reuses same 3-step strategy as WarningAnalysisQueue) ──

        [DataContract]
        private sealed class SamFixResponse
        {
            [DataMember(Name = "canFix")]               public bool   CanFix               { get; set; }
            [DataMember(Name = "summary")]              public string Summary              { get; set; }
            [DataMember(Name = "explanation")]          public string Explanation          { get; set; }
            [DataMember(Name = "originalSnippet")]      public string OriginalSnippet      { get; set; }
            [DataMember(Name = "proposedSnippet")]      public string ProposedSnippet      { get; set; }
            [DataMember(Name = "extractedFileName")]    public string ExtractedFileName    { get; set; }
            [DataMember(Name = "extractedFileContent")] public string ExtractedFileContent { get; set; }
            [DataMember(Name = "unifiedDiff")]          public string UnifiedDiff          { get; set; }
            [DataMember(Name = "riskLevel")]            public string RiskLevel            { get; set; }
        }

        private void ParseLlmResponse(SamTodoItem item, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                item.Status        = SamFixStatus.Failed;
                item.FailureReason = "Empty LLM response.";
                return;
            }

            try
            {
                string json = ExtractJsonBlock(response);
                Log($"[Parse] Extracted {json?.Length ?? 0} chars.");

                SamFixResponse parsed = TryDeserialize(json);
                Log($"[Parse] DataContract: {(parsed != null ? "OK" : "FAILED")}");

                if (parsed == null)
                {
                    Log("[Parse] Regex fallback.");
                    parsed = new SamFixResponse
                    {
                        CanFix               = GetJsonBool(json, "canFix"),
                        Summary              = GetJsonString(json, "summary"),
                        Explanation          = GetJsonString(json, "explanation"),
                        OriginalSnippet      = GetJsonString(json, "originalSnippet"),
                        ProposedSnippet      = GetJsonString(json, "proposedSnippet"),
                        ExtractedFileName    = GetJsonString(json, "extractedFileName"),
                        ExtractedFileContent = GetJsonString(json, "extractedFileContent"),
                        UnifiedDiff          = GetJsonString(json, "unifiedDiff"),
                        RiskLevel            = GetJsonString(json, "riskLevel")
                    };
                }

                item.LlmSummary          = parsed.Summary;
                item.LlmExplanation      = parsed.Explanation;
                item.OriginalSnippet     = parsed.OriginalSnippet;
                item.ProposedSnippet     = parsed.ProposedSnippet;
                item.ExtractedFileName   = parsed.ExtractedFileName;
                item.ExtractedFileContent= parsed.ExtractedFileContent;
                item.UnifiedDiff         = parsed.UnifiedDiff;
                item.RiskLevel           = parsed.RiskLevel;

                bool hasUsableSnippet = !string.IsNullOrWhiteSpace(item.OriginalSnippet) &&
                                        !string.IsNullOrWhiteSpace(item.ProposedSnippet);

                if (!parsed.CanFix && !hasUsableSnippet)
                {
                    // Pure architecture guidance
                    item.Status = SamFixStatus.ArchitectureOnly;
                    return;
                }

                if (!hasUsableSnippet && string.IsNullOrWhiteSpace(item.ExtractedFileContent))
                {
                    item.Status        = SamFixStatus.Failed;
                    item.FailureReason = "LLM response missing code snippets.";
                }
                else
                {
                    item.Status = SamFixStatus.Suggested;
                }
            }
            catch (Exception ex)
            {
                item.Status        = SamFixStatus.Failed;
                item.FailureReason = $"Parse error: {ex.Message}";
            }
        }

        private static string ExtractJsonBlock(string response)
        {
            string text = response.Trim();
            var fence = Regex.Match(text, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.Singleline);
            if (fence.Success) return fence.Groups[1].Value.Trim();
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            if (s >= 0 && e > s) return text.Substring(s, e - s + 1);
            return text;
        }

        private static SamFixResponse TryDeserialize(string json)
        {
            try
            {
                var ser = new DataContractJsonSerializer(typeof(SamFixResponse));
                using (var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return ser.ReadObject(ms) as SamFixResponse;
            }
            catch { return null; }
        }

        private static string GetJsonString(string json, string key)
        {
            string ek = Regex.Escape(key);
            var m = Regex.Match(json, $"\"?{ek}\"?\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*?)\"", RegexOptions.Singleline);
            if (m.Success) return UnescapeJson(m.Groups[1].Value);
            m = Regex.Match(json, $"\"?{ek}\"?\\s*:\\s*\"([\\s\\S]*?)\"\\s*[,}}]", RegexOptions.Singleline);
            if (m.Success) return UnescapeJson(m.Groups[1].Value);
            return "";
        }

        private static string UnescapeJson(string v) =>
            v.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r");

        private static bool GetJsonBool(string json, string key)
        {
            var m = Regex.Match(json, $"\"?{Regex.Escape(key)}\"?\\s*:\\s*\"?(true|false)\"?", RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][SamQueue] {msg}");

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
