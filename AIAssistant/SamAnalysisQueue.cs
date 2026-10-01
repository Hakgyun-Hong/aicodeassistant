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

        // ─── Core processing ────────────────────────────────────────────────

        private async Task ProcessOneAsync(SamTodoItem item, CancellationToken ct)
        {
            try
            {
                // Architecture-only items have no source snippet — skip auto-analysis.
                if (item.Status == SamFixStatus.ArchitectureOnly)
                {
                    item.Status    = SamFixStatus.ArchitectureOnly;
                    item.UpdatedAt = DateTimeOffset.Now;
                    return;
                }

                item.Status    = SamFixStatus.Analyzing;
                item.UpdatedAt = DateTimeOffset.Now;
                _store.NotifyChanged();

                // Read code context
                string codeContext = null;
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    int contextLine = item.LineNo > 0 ? item.LineNo : 1;
                    codeContext = WarningCollector.ReadCodeContext(item.FilePath, contextLine);
                }

                if (string.IsNullOrEmpty(codeContext))
                {
                    // Still try — provide just the metadata, no snippet
                    Log($"[{item.Metric}] No code context available for {item.RelativePath}:{item.LineNo}");
                }

                string systemPrompt = BuildSystemPrompt(item);
                string userContent  = BuildUserContent(item, codeContext);

                ct.ThrowIfCancellationRequested();

                string response = await LlmClient.AskAsync(systemPrompt, userContent);
                Log($"[{item.Metric}] Response: {response?.Length ?? 0} chars.");
                Log($"[{item.Metric}] Preview: {Truncate(response, 300)}");

                ct.ThrowIfCancellationRequested();

                ParseLlmResponse(item, response);

                if (item.Status != SamFixStatus.Failed)
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

        // ─── Prompt Construction ────────────────────────────────────────────

        private static string BuildSystemPrompt(SamTodoItem item)
        {
            string metricGuidance = GetMetricGuidance(item.Metric);

            return
$@"You are a senior software engineer specializing in code quality improvement for Samsung SW quality metrics (SAM indicators).
Your job is to propose the smallest, safest fix for the given SAM metric violation.

METRIC CONTEXT:
{metricGuidance}

RULES:
- Do not rewrite unrelated code.
- Preserve existing functionality exactly.
- Keep the same indentation and coding style.
- If the violation cannot be fixed by a local code change (e.g. architecture-level issue), set canFix to false and provide architectural guidance in explanation.

JSON FORMATTING RULES (critical — the response will be machine-parsed):
- Return ONLY a valid JSON object. No markdown, no text outside the JSON.
- Escape all newlines inside strings as \n, backslashes as \\, quotes as \"".
- originalSnippet MUST be a verbatim character-for-character substring of the Code Context.
- proposedSnippet MUST be the complete drop-in replacement preserving indentation.

Required JSON fields:
- canFix          (bool)   : true if a direct code fix is possible.
- summary         (string) : one-line description of the fix.
- explanation     (string) : detailed reasoning.
- originalSnippet (string) : exact lines to replace (JSON-escaped). Empty string if canFix=false.
- proposedSnippet (string) : replacement code (JSON-escaped). Empty string if canFix=false.
- unifiedDiff     (string) : unified diff of the change. Empty string if canFix=false.
- riskLevel       (string) : Low, Medium, or High.";
        }

        private static string GetMetricGuidance(string metric)
        {
            switch (metric)
            {
                case "CC":
                    return "CyclomaticComplexity — The function has too many decision branches (if/else/switch/loops). " +
                           "Reduce by: extracting sub-functions, using early returns, guard clauses, or table-driven logic. " +
                           "Target: complexity ≤ 10.";
                case "DC":
                    return "DuplicateCode — Identical or near-identical code blocks exist in multiple places. " +
                           "Fix by extracting into a shared function, template, or macro that both callers use.";
                case "MCD":
                    return "ModuleCircularDependency — Two or more modules depend on each other cyclically. " +
                           "This is an architecture-level issue. Suggest introducing an abstraction layer or dependency inversion.";
                case "CBO":
                    return "CouplingBetweenObjects — A class references too many other classes. " +
                           "Reduce by applying Dependency Inversion Principle, extracting interfaces, or splitting responsibilities.";
                case "LOC":
                    return "LinesOfCode — A class or function is too long. " +
                           "Extract cohesive responsibilities into separate classes or functions.";
                case "GV":
                    return "GlobalVariable — A global variable is accessed by multiple functions. " +
                           "Encapsulate it: move into a struct/class, pass as parameter, or use accessor functions.";
                case "PP":
                    return "PredefinedPreprocessor — Overuse of preprocessor macros. " +
                           "Replace with typed constants, inline functions, or template functions where possible.";
                case "NCS":
                    return "NestedControlStatements — Control flow (if/for/while) is nested too deeply. " +
                           "Apply guard clauses (early return), extract inner loops into functions, or invert conditions.";
                case "DepC":
                    return "DependencyComplexity — A module has too many fan-out dependencies. " +
                           "Reduce by applying Facade pattern, splitting the module, or using dependency injection.";
                case "GM":
                    return "GodModule — A module has too many functions and lines (does too much). " +
                           "Split into smaller, single-responsibility modules.";
                default:
                    return $"SAM metric '{metric}' violation. Apply best practices to resolve the quality issue.";
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
                sb.AppendLine("(No code context available — provide architectural guidance only.)");
            }

            return sb.ToString();
        }

        // ─── Response Parsing (reuses same 3-step strategy as WarningAnalysisQueue) ──

        [DataContract]
        private sealed class SamFixResponse
        {
            [DataMember(Name = "canFix")]          public bool   CanFix          { get; set; }
            [DataMember(Name = "summary")]         public string Summary         { get; set; }
            [DataMember(Name = "explanation")]     public string Explanation     { get; set; }
            [DataMember(Name = "originalSnippet")] public string OriginalSnippet { get; set; }
            [DataMember(Name = "proposedSnippet")] public string ProposedSnippet { get; set; }
            [DataMember(Name = "unifiedDiff")]     public string UnifiedDiff     { get; set; }
            [DataMember(Name = "riskLevel")]       public string RiskLevel       { get; set; }
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
                        CanFix          = GetJsonBool(json, "canFix"),
                        Summary         = GetJsonString(json, "summary"),
                        Explanation     = GetJsonString(json, "explanation"),
                        OriginalSnippet = GetJsonString(json, "originalSnippet"),
                        ProposedSnippet = GetJsonString(json, "proposedSnippet"),
                        UnifiedDiff     = GetJsonString(json, "unifiedDiff"),
                        RiskLevel       = GetJsonString(json, "riskLevel")
                    };
                }

                if (!parsed.CanFix)
                {
                    // Not directly fixable — mark as ArchitectureOnly with the explanation stored.
                    item.Status        = SamFixStatus.ArchitectureOnly;
                    item.LlmExplanation = parsed.Explanation;
                    item.LlmSummary     = parsed.Summary;
                    item.RiskLevel      = parsed.RiskLevel;
                    return;
                }

                item.LlmSummary      = parsed.Summary;
                item.LlmExplanation  = parsed.Explanation;
                item.OriginalSnippet = parsed.OriginalSnippet;
                item.ProposedSnippet = parsed.ProposedSnippet;
                item.UnifiedDiff     = parsed.UnifiedDiff;
                item.RiskLevel       = parsed.RiskLevel;

                if (string.IsNullOrWhiteSpace(item.OriginalSnippet) ||
                    string.IsNullOrWhiteSpace(item.ProposedSnippet))
                {
                    item.Status        = SamFixStatus.Failed;
                    item.FailureReason = "LLM response missing originalSnippet or proposedSnippet.";
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
