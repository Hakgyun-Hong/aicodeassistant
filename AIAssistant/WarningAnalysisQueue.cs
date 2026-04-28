using System;
using System.Collections.Concurrent;
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

                string userContent = $@"Warning Code: {item.Code}
Warning Message: {item.Message}
File: {item.FilePath}
Line: {item.Line}
Column: {item.Column}
Project: {item.Project}

Code Context:
```
{codeContext}
```

Return a JSON object with these fields:
- canFix (bool)
- summary (string, one-line)
- explanation (string)
- originalSnippet (string, exact lines to replace)
- proposedSnippet (string, replacement code)
- unifiedDiff (string)
- riskLevel (string: Low/Medium/High)";

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

        /// <summary>
        /// Parse LLM JSON response and populate the WarningTodoItem fields.
        /// Handles both clean JSON and JSON embedded in markdown code blocks.
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
                // Extract JSON from response (may be wrapped in ```json ... ```)
                string json = response.Trim();
                var jsonMatch = Regex.Match(json, @"```(?:json)?\s*\n?([\s\S]*?)```");
                if (jsonMatch.Success)
                {
                    json = jsonMatch.Groups[1].Value.Trim();
                }

                // Simple JSON parsing without System.Text.Json (which isn't in .NET 4.7.2)
                // Parse key fields manually using regex
                bool canFix = GetJsonBool(json, "canFix");

                if (!canFix)
                {
                    item.Status = WarningFixStatus.Failed;
                    item.FailureReason = "LLM determined this warning cannot be safely fixed.";
                    return;
                }

                item.LlmSummary = GetJsonString(json, "summary");
                item.LlmExplanation = GetJsonString(json, "explanation");
                item.OriginalSnippet = GetJsonString(json, "originalSnippet");
                item.ProposedSnippet = GetJsonString(json, "proposedSnippet");
                item.UnifiedDiff = GetJsonString(json, "unifiedDiff");
                item.RiskLevel = GetJsonString(json, "riskLevel");

                if (string.IsNullOrWhiteSpace(item.OriginalSnippet) ||
                    string.IsNullOrWhiteSpace(item.ProposedSnippet))
                {
                    item.Status = WarningFixStatus.Failed;
                    item.FailureReason = "LLM response missing originalSnippet or proposedSnippet.";
                    return;
                }
            }
            catch (Exception ex)
            {
                item.Status = WarningFixStatus.Failed;
                item.FailureReason = $"Failed to parse LLM response: {ex.Message}";
            }
        }

        /// <summary>
        /// Extract a string value from a JSON object by key name.
        /// Handles escaped characters in JSON strings.
        /// </summary>
        private static string GetJsonString(string json, string key)
        {
            // Match "key": "value" with possible escaped quotes inside
            var match = Regex.Match(json,
                $"\"?{key}\"?\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.Singleline);
            if (!match.Success) return "";

            string value = match.Groups[1].Value;
            // Unescape common JSON escape sequences
            value = value.Replace("\\n", "\n")
                         .Replace("\\t", "\t")
                         .Replace("\\\"", "\"")
                         .Replace("\\\\", "\\");
            return value;
        }

        private static bool GetJsonBool(string json, string key)
        {
            var match = Regex.Match(json, $"\"?{key}\"?\\s*:\\s*\"?(true|false)\"?", RegexOptions.IgnoreCase);
            return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
