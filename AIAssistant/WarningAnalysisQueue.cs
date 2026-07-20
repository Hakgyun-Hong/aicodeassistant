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

        public void Cancel()
        {
            _cts?.Cancel();
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

                ct.ThrowIfCancellationRequested();

                // Parse the JSON response
                ParseLlmResponse(item, response);

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

                // Step 2 — try structured deserialization (handles properly-escaped JSON).
                WarningFixResponse parsed = TryDeserialize(json);

                // Step 3 — fall back to lenient regex parsing.
                if (parsed == null)
                {
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
                return fenceMatch.Groups[1].Value.Trim();

            // Fall back: find the outermost { ... } block
            int start = text.IndexOf('{');
            int end   = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                return text.Substring(start, end - start + 1);

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
                    return serializer.ReadObject(ms) as WarningFixResponse;
                }
            }
            catch
            {
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
                return UnescapeJsonString(match.Groups[1].Value);

            // Pattern 2 — lenient: capture until the next closing-quote before , or }
            match = Regex.Match(json,
                $"\"?{escapedKey}\"?\\s*:\\s*\"([\\s\\S]*?)\"\\s*[,}}]",
                RegexOptions.Singleline);
            if (match.Success)
                return UnescapeJsonString(match.Groups[1].Value);

            return "";
        }

        /// <summary>
        /// Unescape standard JSON escape sequences in a parsed string value.
        /// Order matters: \\\\ must be unescaped last.
        /// </summary>
        private static string UnescapeJsonString(string value)
        {
            return value
                .Replace("\\n",  "\n")
                .Replace("\\t",  "\t")
                .Replace("\\r",  "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private static bool GetJsonBool(string json, string key)
        {
            var match = Regex.Match(json,
                $"\"?{Regex.Escape(key)}\"?\\s*:\\s*\"?(true|false)\"?",
                RegexOptions.IgnoreCase);
            return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
