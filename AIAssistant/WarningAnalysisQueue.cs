using System;
using System.Collections.Concurrent;
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
    /// Background queue that sends warnings to LLM for analysis.
    /// Uses SemaphoreSlim to limit concurrent requests.
    /// </summary>
    public sealed class WarningAnalysisQueue
    {
        private readonly ConcurrentQueue<WarningTodoItem> _queue = new ConcurrentQueue<WarningTodoItem>();
        private readonly SemaphoreSlim _parallelism;
        private readonly WarningTodoStore _store;
        private CancellationTokenSource _cts;
        private int _totalEnqueued;
        private int _totalProcessed;

        public event Action<int, int> ProgressChanged;

        public WarningAnalysisQueue(WarningTodoStore store, int maxParallel = 3)
        {
            _store = store;
            _parallelism = new SemaphoreSlim(maxParallel, maxParallel);
        }

        public void Enqueue(WarningTodoItem item)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _totalEnqueued);
        }

        /// <summary>
        /// Signal cancellation to all in-flight tasks and drain the queue
        /// so that remaining items are not processed by a subsequent ProcessAllAsync.
        /// Callers should revert any Analyzing items to PendingAnalysis on the UI side.
        /// </summary>
        public void Cancel()
        {
            _cts?.Cancel();

            // Drain unprocessed items from the queue so they don't run later.
            while (_queue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Reset a single item's LLM fields and re-run analysis for it alone.
        /// Useful when the user is not satisfied with the current suggestion.
        /// Respects the existing parallelism semaphore.
        /// </summary>
        public async Task ReanalyzeAsync(WarningTodoItem item)
        {
            if (item == null) return;

            // Clear previous LLM result so the item is treated as fresh.
            item.LlmSummary     = null;
            item.LlmExplanation = null;
            item.OriginalSnippet= null;
            item.ProposedSnippet= null;
            item.UnifiedDiff    = null;
            item.RiskLevel      = null;
            item.FailureReason  = null;
            item.Status         = WarningFixStatus.PendingAnalysis;
            item.UpdatedAt      = DateTimeOffset.Now;
            _store.NotifyChanged();

            Log($"[{item.Code}] ReanalyzeAsync started.");

            // Track progress for this single item
            Interlocked.Increment(ref _totalEnqueued);

            // Use the class-level CTS so Cancel() also covers re-analyze requests.
            if (_cts == null || _cts.IsCancellationRequested)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            // Acquire semaphore (ProcessOneAsync releases it in its finally block).
            await _parallelism.WaitAsync(_cts.Token);
            await ProcessOneAsync(item, _cts.Token);
        }

        /// <summary>
        /// Batch-reset a collection of items and enqueue them all for reprocessing.
        /// Clears all LLM fields and marks each item as <see cref="WarningFixStatus.PendingAnalysis"/>.
        /// Call <see cref="ProcessAllAsync"/> afterwards to actually run the queue.
        /// </summary>
        public void ResetAndEnqueue(System.Collections.Generic.IEnumerable<WarningTodoItem> items)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (item == null) continue;
                item.LlmSummary     = null;
                item.LlmExplanation = null;
                item.OriginalSnippet= null;
                item.ProposedSnippet= null;
                item.UnifiedDiff    = null;
                item.RiskLevel      = null;
                item.FailureReason  = null;
                item.Status         = WarningFixStatus.PendingAnalysis;
                item.UpdatedAt      = DateTimeOffset.Now;
                Enqueue(item);
                Log($"[{item.Code}] ResetAndEnqueue: queued for retry.");
            }
            _store.NotifyChanged();
        }

        /// <summary>
        /// Process all enqueued items. Returns when all are done or cancelled.
        /// </summary>

        public async Task ProcessAllAsync()
        {
            // Dispose the previous token source to avoid resource leaks.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _totalProcessed = 0;

            var tasks = new ConcurrentBag<Task>();

            while (_queue.TryDequeue(out var item))
            {
                if (_cts.IsCancellationRequested) break;

                await _parallelism.WaitAsync(_cts.Token);
                var task = ProcessOneAsync(item, _cts.Token);
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel
            }
        }

        private async Task ProcessOneAsync(WarningTodoItem item, CancellationToken ct)
        {
            try
            {
                item.Status = WarningFixStatus.Analyzing;
                item.UpdatedAt = DateTimeOffset.Now;
                _store.NotifyChanged();

                // Read code context around the warning
                string codeContext = WarningCollector.ReadCodeContext(item.FilePath, item.Line);
                if (string.IsNullOrEmpty(codeContext))
                {
                    item.Status = WarningFixStatus.Failed;
                    item.FailureReason = "Could not read source file.";
                    _store.NotifyChanged();
                    return;
                }

                // Build the prompt
                string systemPrompt = PromptConfig.Instance.WarningFixPrompt;

                // Include file extension so the LLM can infer the language.
                string fileExt = System.IO.Path.GetExtension(item.FilePath);

                // The JSON schema is defined in the system prompt — do not repeat it here.
                string userContent = $@"Language hint: {fileExt}
Warning Code: {item.Code}
Warning Message: {item.Message}
File: {item.FilePath}
Line: {item.Line}, Column: {item.Column}
Project: {item.Project}

Code Context (warning is near line {item.Line}):
```{fileExt.TrimStart('.')}
{codeContext}
```";

                ct.ThrowIfCancellationRequested();

                string response = await LlmClient.AskAsync(systemPrompt, userContent);

                Log($"[{item.Code}] LLM response received: {response?.Length ?? 0} chars.");
                Log($"[{item.Code}] Response preview: {Truncate(response, 300)}");

                ct.ThrowIfCancellationRequested();

                // Parse the JSON response
                ParseLlmResponse(item, response);

                Log($"[{item.Code}] Parse complete → Status={item.Status}" +
                    (item.FailureReason != null ? $", Reason: {item.FailureReason}" : ""));

                if (item.Status != WarningFixStatus.Failed)
                {
                    item.Status = WarningFixStatus.Suggested;
                }
                item.UpdatedAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = "Cancelled.";
            }
            catch (Exception ex)
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = ex.Message;
                item.UpdatedAt = DateTimeOffset.Now;
            }
            finally
            {
                _parallelism.Release();
                Interlocked.Increment(ref _totalProcessed);
                ProgressChanged?.Invoke(_totalProcessed, _totalEnqueued);
                _store.NotifyChanged();
            }
        }

        // ───────────────────────────────────────────────────────────────
        // LLM Response Parsing
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Data contract matching the JSON object the LLM must return.
        /// Allows DataContractJsonSerializer to deserialize the response
        /// without requiring external NuGet packages.
        /// </summary>
        [DataContract]
        private sealed class WarningFixResponse
        {
            [DataMember(Name = "canFix")]
            public bool CanFix { get; set; }

            [DataMember(Name = "summary")]
            public string Summary { get; set; }

            [DataMember(Name = "explanation")]
            public string Explanation { get; set; }

            [DataMember(Name = "originalSnippet")]
            public string OriginalSnippet { get; set; }

            [DataMember(Name = "proposedSnippet")]
            public string ProposedSnippet { get; set; }

            [DataMember(Name = "unifiedDiff")]
            public string UnifiedDiff { get; set; }

            [DataMember(Name = "riskLevel")]
            public string RiskLevel { get; set; }
        }

        /// <summary>
        /// Parse LLM JSON response and populate the WarningTodoItem fields.
        /// Strategy:
        ///   1. Extract the JSON block (strips ```json fences if present).
        ///   2. Deserialize via DataContractJsonSerializer (strict, correct).
        ///   3. Fall back to lenient regex parsing when the LLM returns
        ///      malformed JSON (e.g. real newlines inside string values).
        /// </summary>
        private void ParseLlmResponse(WarningTodoItem item, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = "Empty LLM response.";
                return;
            }

            try
            {
                // Step 1 — extract the JSON object from surrounding text.
                string json = ExtractJsonBlock(response);
                Log($"[Parse] Step 1 extraction: {json?.Length ?? 0} chars. Preview: {Truncate(json, 200)}");

                // Step 2 — try structured deserialization (handles properly-escaped JSON).
                WarningFixResponse parsed = TryDeserialize(json);
                Log($"[Parse] Step 2 DataContractJsonSerializer: {(parsed != null ? "SUCCESS" : "FAILED")}");

                // Step 3 — fall back to lenient regex parsing.
                if (parsed == null)
                {
                    Log("[Parse] Step 3 Regex fallback — parsing fields individually.");
                    parsed = new WarningFixResponse
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

                Log($"[Parse] Result: canFix={parsed.CanFix}, riskLevel={parsed.RiskLevel ?? "(empty)"}, " +
                    $"originalSnippet={!string.IsNullOrWhiteSpace(parsed.OriginalSnippet)}, " +
                    $"proposedSnippet={!string.IsNullOrWhiteSpace(parsed.ProposedSnippet)}");

                if (!parsed.CanFix)
                {
                    item.Status = WarningFixStatus.Failed;
                    item.FailureReason = "LLM determined this warning cannot be safely fixed.";
                    return;
                }

                item.LlmSummary       = parsed.Summary;
                item.LlmExplanation   = parsed.Explanation;
                item.OriginalSnippet  = parsed.OriginalSnippet;
                item.ProposedSnippet  = parsed.ProposedSnippet;
                item.UnifiedDiff      = parsed.UnifiedDiff;
                item.RiskLevel        = parsed.RiskLevel;

                if (string.IsNullOrWhiteSpace(item.OriginalSnippet) ||
                    string.IsNullOrWhiteSpace(item.ProposedSnippet))
                {
                    item.Status = WarningFixStatus.Failed;
                    item.FailureReason = "LLM response missing originalSnippet or proposedSnippet.";
                }
            }
            catch (Exception ex)
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = $"Failed to parse LLM response: {ex.Message}";
            }
        }

        /// <summary>
        /// Extract a JSON object from raw LLM output.
        /// Strips ```json ... ``` code fences; otherwise finds the outermost { } block.
        /// </summary>
        private static string ExtractJsonBlock(string response)
        {
            string text = response.Trim();

            // Try ```json ... ``` or plain ``` ... ``` fence
            var fenceMatch = Regex.Match(text, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.Singleline);
            if (fenceMatch.Success)
            {
                Log("[Extract] Method: code-fence (```json...```).");
                return fenceMatch.Groups[1].Value.Trim();
            }

            // Fall back: find the outermost { ... } block
            int start = text.IndexOf('{');
            int end   = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                Log("[Extract] Method: brace-boundary search.");
                return text.Substring(start, end - start + 1);
            }

            Log("[Extract] WARNING: No JSON structure found — using raw response text.");
            return text;
        }

        /// <summary>
        /// Attempt to deserialize <paramref name="json"/> using DataContractJsonSerializer.
        /// Returns null if the JSON is malformed or missing required fields.
        /// </summary>
        private static WarningFixResponse TryDeserialize(string json)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(WarningFixResponse));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var result = serializer.ReadObject(ms) as WarningFixResponse;
                    Log("[Deserialize] DataContractJsonSerializer succeeded.");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"[Deserialize] DataContractJsonSerializer failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Lenient regex-based extraction of a JSON string value by key.
        /// Tries two patterns:
        ///   1. Standard: value between quotes with \" escapes.
        ///   2. Fallback: for LLM output that embeds real newlines inside strings.
        /// </summary>
        private static string GetJsonString(string json, string key)
        {
            string escapedKey = Regex.Escape(key);

            // Pattern 1 — standard JSON string (handles backslash-escaped quotes)
            var match = Regex.Match(json,
                $"\"?{escapedKey}\"?\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*?)\"",
                RegexOptions.Singleline);
            if (match.Success)
            {
                Log($"[Regex] '{key}': Pattern 1 (standard) matched.");
                return UnescapeJsonString(match.Groups[1].Value);
            }

            // Pattern 2 — lenient: capture until the next closing-quote before , or }
            match = Regex.Match(json,
                $"\"?{escapedKey}\"?\\s*:\\s*\"([\\s\\S]*?)\"\\s*[,}}]",
                RegexOptions.Singleline);
            if (match.Success)
            {
                Log($"[Regex] '{key}': Pattern 2 (lenient) matched.");
                return UnescapeJsonString(match.Groups[1].Value);
            }

            Log($"[Regex] '{key}': NO MATCH — field will be empty.");
            return "";
        }

        /// <summary>
        /// Unescape standard JSON escape sequences in a parsed string value.
        /// Order matters: \\\\ must be unescaped last.
        /// </summary>
        private static string UnescapeJsonString(string value)
        {
            // IMPORTANT: \\ must be unescaped FIRST so that sequences like \\n
            // (a literal backslash followed by 'n', not a newline) are not
            // misinterpreted by the subsequent \n → newline replacement.
            return value
                .Replace("\\\\", "\\")   // ① \\ → single backslash  (must be first)
                .Replace("\\\"", "\"")   // ② \" → double-quote
                .Replace("\\n",  "\n")   // ③ \n → newline
                .Replace("\\t",  "\t")   // ④ \t → tab
                .Replace("\\r",  "\r");  // ⑤ \r → carriage return
        }

        private static bool GetJsonBool(string json, string key)
        {
            var match = Regex.Match(json,
                $"\"?{Regex.Escape(key)}\"?\\s*:\\s*\"?(true|false)\"?",
                RegexOptions.IgnoreCase);
            return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // ─── Diagnostic helpers ──────────────────────────────────────────────

        /// <summary>
        /// Write a diagnostic message to the VS Debug Output window.
        /// Visible under Debug > Windows > Output > [Show output from: Debug] while running.
        /// Use <c>Debug.Listeners</c> or a log viewer like DebugView to capture outside VS.
        /// </summary>
        private static void Log(string message)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][LLMParse] {message}");

        /// <summary>
        /// Return the first <paramref name="maxLen"/> characters of <paramref name="text"/>,
        /// appending "…" when truncated. Returns "(empty)" for null/empty input.
        /// </summary>
        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…";
        }
    }
}
