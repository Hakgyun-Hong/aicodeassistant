using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIAssistant
{
    /// <summary>
    /// Sends ManualBlock + SourceTrace + UiElementInfo context to LLM
    /// and fills in the Content / Steps for each block.
    ///
    /// Key design:
    ///   - LLM receives structured context (NOT raw source code)
    ///   - "Inference prohibition" instruction: don't explain what source code doesn't confirm
    ///   - CanExecute analysis → automatic "사용 조건" section
    ///   - Respects confidence threshold — low-confidence blocks are flagged for human review
    /// </summary>
    public sealed class ManualGenerator
    {
        private readonly int _maxParallel;

        public event Action<int, int> ProgressChanged;

        public ManualGenerator(int maxParallel = 3)
        {
            _maxParallel = maxParallel;
        }

        // ─── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Generate LLM descriptions for all ControlItem blocks in the document.
        /// Other block types (Heading, Screenshot) are left as-is.
        /// </summary>
        public async Task GenerateAllAsync(ManualDocument doc, System.Threading.CancellationToken ct)
        {
            var controlBlocks = doc.Blocks
                .Where(b => b.Type == ManualBlockType.ControlItem && b.IsIncluded)
                .ToList();

            var semaphore = new System.Threading.SemaphoreSlim(_maxParallel, _maxParallel);
            int done = 0;

            var tasks = controlBlocks.Select(async block =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var el    = doc.Elements.FirstOrDefault(e => e.UniqueId == block.ElementUniqueId);
                    var trace = doc.Traces.FirstOrDefault(t => t.ElementUniqueId == block.ElementUniqueId);

                    await FillBlockAsync(block, el, trace);

                    System.Threading.Interlocked.Increment(ref done);
                    ProgressChanged?.Invoke(done, controlBlocks.Count);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    block.Content    = $"(분석 실패: {ex.Message})";
                    block.Confidence = 0;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
        }

        /// <summary>Regenerate a single block (for "Regenerate" button in editor).</summary>
        public async Task RegenerateBlockAsync(ManualBlock block, UiElementInfo el, SourceTrace trace)
        {
            block.Content    = "";
            block.Confidence = 0;
            await FillBlockAsync(block, el, trace);
        }

        // ─── Core: build prompt + call LLM ─────────────────────────────────────

        private async Task FillBlockAsync(ManualBlock block, UiElementInfo el, SourceTrace trace)
        {
            string system = BuildSystemPrompt();
            string user   = BuildUserContent(block, el, trace);

            string response = await LlmClient.AskAsync(system, user);

            ParseLlmResponse(block, response);
        }

        // ─── Prompt construction ───────────────────────────────────────────────

        private static string BuildSystemPrompt() =>
@"당신은 산업용 소프트웨어 매뉴얼을 작성하는 전문가입니다.
주어진 UI 정보와 코드 추적 데이터를 바탕으로 사용자 매뉴얼 항목을 작성합니다.

규칙:
1. 소스 코드에서 확인되지 않은 기능은 추론하여 작성하지 마세요.
2. 기술적 클래스명/메서드명은 사용자에게 노출하지 마세요. 기능의 의미만 설명합니다.
3. 반드시 JSON으로만 응답하세요 (마크다운 코드블록 없이).
4. 설명은 사용자 관점의 자연스러운 한국어로 작성합니다.
5. CanExecute 조건이 있으면 반드시 '사용 가능 조건' 섹션을 포함합니다.

JSON 형식:
{
  ""description"": ""기능 설명 (2-4문장)"",
  ""conditions"": ""사용 가능 조건 (없으면 null)"",
  ""steps"": [""단계1"", ""단계2""],
  ""notes"": ""주의사항 (없으면 null)"",
  ""confidence"": 0.0~1.0
}";

        private static string BuildUserContent(ManualBlock block, UiElementInfo el, SourceTrace trace)
        {
            var sb = new StringBuilder();

            // ── Section 1: Runtime UI ──
            sb.AppendLine("=== RUNTIME UI ===");
            if (el != null)
            {
                sb.AppendLine($"Name: {el.Name}");
                sb.AppendLine($"Type: {el.ControlType}");
                sb.AppendLine($"Enabled: {el.IsEnabled}");
                if (!string.IsNullOrEmpty(el.Value))
                    sb.AppendLine($"Current Value: {el.Value}");
                if (!string.IsNullOrEmpty(el.HelpText))
                    sb.AppendLine($"HelpText: {el.HelpText}");
                if (el.SupportedPatterns.Count > 0)
                    sb.AppendLine($"Supported Interactions: {string.Join(", ", el.SupportedPatterns)}");
            }
            else
            {
                sb.AppendLine($"Name: {block.Title}");
                sb.AppendLine("(Runtime element not available)");
            }

            // ── Section 2: Source Trace ──
            if (trace != null)
            {
                sb.AppendLine();
                sb.AppendLine("=== SOURCE TRACE ===");

                if (!string.IsNullOrEmpty(trace.XamlFragment))
                    sb.AppendLine($"XAML: {trace.XamlFragment}");

                if (!string.IsNullOrEmpty(trace.BindingPath))
                    sb.AppendLine($"Command Binding: {trace.BindingPath}");

                if (!string.IsNullOrEmpty(trace.ViewModelTypeName))
                    sb.AppendLine($"ViewModel: {trace.ViewModelTypeName}");

                if (!string.IsNullOrEmpty(trace.ExecuteMethodName))
                {
                    sb.AppendLine($"Execute: {trace.ExecuteMethodName}()");

                    if (trace.CallGraph.Count > 0)
                    {
                        sb.AppendLine("Call Graph:");
                        foreach (var sym in trace.CallGraph.Take(6))
                            sb.AppendLine($"  → {sym.Symbol}");
                    }
                }

                if (!string.IsNullOrEmpty(trace.CanExecuteCondition))
                {
                    sb.AppendLine();
                    sb.AppendLine("=== CAN EXECUTE CONDITION ===");
                    sb.AppendLine(trace.CanExecuteCondition);
                }

                if (trace.Evidence.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== SOURCE EVIDENCE ===");
                    foreach (var ev in trace.Evidence)
                        sb.AppendLine($"  {ev}");
                }
            }

            // ── Section 3: Context ──
            sb.AppendLine();
            sb.AppendLine("=== CONTEXT ===");
            sb.AppendLine($"Window: {block.WindowName}");
            if (!string.IsNullOrEmpty(block.TabContext))
                sb.AppendLine($"Tab: {block.TabContext}");
            if (block.CalloutNumber.HasValue)
                sb.AppendLine($"Callout Number: ①②③... #{block.CalloutNumber}");

            return sb.ToString();
        }

        // ─── Response parsing ──────────────────────────────────────────────────

        [DataContract]
        private sealed class LlmManualResponse
        {
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "conditions")]  public string Conditions  { get; set; }
            [DataMember(Name = "steps")]       public List<string> Steps { get; set; }
            [DataMember(Name = "notes")]       public string Notes       { get; set; }
            [DataMember(Name = "confidence")]  public double Confidence  { get; set; }
        }

        private static void ParseLlmResponse(ManualBlock block, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                block.Content    = "(LLM 응답 없음)";
                block.Confidence = 0;
                return;
            }

            try
            {
                string json   = ExtractJson(response);
                var parsed    = TryDeserialize(json);

                if (parsed == null)
                {
                    // Regex fallback
                    parsed = new LlmManualResponse
                    {
                        Description = GetString(json, "description"),
                        Conditions  = GetString(json, "conditions"),
                        Notes       = GetString(json, "notes"),
                        Confidence  = GetDouble(json, "confidence")
                    };
                }

                // Build structured content
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(parsed.Description))
                    sb.AppendLine(parsed.Description);

                if (!string.IsNullOrEmpty(parsed.Conditions) &&
                    parsed.Conditions != "null")
                {
                    sb.AppendLine();
                    sb.AppendLine("사용 가능 조건:");
                    sb.AppendLine(parsed.Conditions);
                }

                if (!string.IsNullOrEmpty(parsed.Notes) && parsed.Notes != "null")
                {
                    sb.AppendLine();
                    sb.AppendLine($"⚠️ {parsed.Notes}");
                }

                block.Content    = sb.ToString().Trim();
                block.Steps      = parsed.Steps ?? new List<string>();
                block.Confidence = parsed.Confidence > 0 ? parsed.Confidence : 0.7;
            }
            catch
            {
                // Last-resort: use raw response as content
                block.Content    = response.Trim();
                block.Confidence = 0.5;
            }
        }

        private static string ExtractJson(string text)
        {
            text = text.Trim();
            var fence = Regex.Match(text, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.Singleline);
            if (fence.Success) return fence.Groups[1].Value.Trim();
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            if (s >= 0 && e > s) return text.Substring(s, e - s + 1);
            return text;
        }

        private static LlmManualResponse TryDeserialize(string json)
        {
            try
            {
                var ser = new DataContractJsonSerializer(typeof(LlmManualResponse));
                using (var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return ser.ReadObject(ms) as LlmManualResponse;
            }
            catch { return null; }
        }

        private static string GetString(string json, string key)
        {
            var m = Regex.Match(json,
                $"\"?{Regex.Escape(key)}\"?\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*?)\"",
                RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value
                .Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\") : "";
        }

        private static double GetDouble(string json, string key)
        {
            var m = Regex.Match(json,
                $"\"?{Regex.Escape(key)}\"?\\s*:\\s*([0-9.]+)");
            return m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double d) ? d : 0.7;
        }
    }
}
