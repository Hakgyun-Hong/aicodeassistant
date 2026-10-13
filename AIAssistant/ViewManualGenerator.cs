using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIAssistant
{
    /// <summary>
    /// Generates the manual content for ONE View (page) by sending the LLM:
    ///   - menu path / navigation context
    ///   - numbered callouts + visible texts from the captured screen (runtime evidence)
    ///   - View XAML, code-behind (if non-trivial), ViewModel source (static evidence)
    /// and filling Summary / Controls / Procedures / Notes from a JSON response.
    /// </summary>
    public sealed class ViewManualGenerator
    {
        // Character budgets per source (rough token control for local LLMs).
        public int MaxXamlChars      { get; set; } = 14000;
        public int MaxCodeBehindChars{ get; set; } = 3000;
        public int MaxViewModelChars { get; set; } = 16000;

        public async Task GenerateAsync(ManualProject project, ManualPage page, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string user = BuildUserPrompt(project, page);
            string response = await LlmClient.AskAsync(SystemPrompt, user);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("LLM 응답이 비어 있습니다.");
            if (response.StartsWith("Error:", StringComparison.Ordinal))
                throw new InvalidOperationException(response.Substring(6).Trim());

            Apply(page, response);
            page.Status = PageStatus.Generated;
            page.StatusMessage = null;
        }

        // ─── Prompt ─────────────────────────────────────────────────────────

        public const string SystemPrompt =
@"당신은 산업용 장비 제어 소프트웨어(WPF/Prism)의 사용자 매뉴얼을 작성하는 테크니컬 라이터입니다.
하나의 화면(View)에 대해 실제 운영자가 읽을 매뉴얼 내용을 작성합니다.

규칙:
1. 제공된 XAML / ViewModel 소스와 화면 정보에서 확인되는 내용만 작성합니다. 추측하지 마세요.
2. 클래스명, 메서드명, 변수명, 바인딩 경로 같은 기술 용어는 쓰지 말고, 화면에 보이는 라벨/버튼 이름으로 설명합니다.
3. 버튼의 CanExecute 조건이나 IsEnabled 바인딩이 있으면 '사용 조건'을 컨트롤 설명에 포함합니다.
4. 컨트롤 설명은 화면의 번호(callout)와 연결합니다. 번호가 없는 중요한 컨트롤도 callout을 null로 하여 포함할 수 있습니다.
5. 자연스러운 한국어 존댓말(~합니다)로 작성합니다.
6. 반드시 아래 JSON 형식으로만 응답합니다. 마크다운 코드블록이나 다른 텍스트를 붙이지 마세요.

{
  ""summary"": ""이 화면의 목적과 주요 기능 (2~4문장)"",
  ""layout"": [ { ""area"": ""영역 이름"", ""description"": ""영역 설명"" } ],
  ""controls"": [ { ""callout"": 1, ""name"": ""화면에 보이는 컨트롤 이름"", ""description"": ""기능 설명. 사용 조건 포함"" } ],
  ""procedures"": [ { ""title"": ""작업 이름"", ""steps"": [ ""단계 1"", ""단계 2"" ] } ],
  ""notes"": [ ""주의사항 (없으면 빈 배열)"" ]
}";

        public string BuildUserPrompt(ManualProject project, ManualPage page)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== 화면 정보 ===");
            if (!string.IsNullOrEmpty(project?.AppName)) sb.AppendLine($"프로그램: {project.AppName}");
            sb.AppendLine($"메뉴 경로: {page.PathText}");
            sb.AppendLine($"메뉴 이름: {page.DisplayName}");
            if (page.IsHome) sb.AppendLine("홈 화면: 예 (프로그램 시작 시 표시)");
            if (page.Children.Count > 0)
                sb.AppendLine($"하위 메뉴: {string.Join(", ", page.Children.Select(c => c.DisplayName))}");

            if (page.Callouts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== 스크린샷의 번호 표시(callout) ===");
                foreach (var c in page.Callouts.OrderBy(c => c.Number))
                {
                    var el = page.Elements.FirstOrDefault(e => e.Id == c.ElementId);
                    string type = el?.ControlType ?? "";
                    string aid  = string.IsNullOrEmpty(el?.AutomationId) ? "" : $" (AutomationId={el.AutomationId})";
                    string dis  = el != null && !el.IsEnabled ? " [비활성]" : "";
                    sb.AppendLine($"{c.Number}. [{type}] {c.Label}{aid}{dis}");
                }
            }

            var texts = page.Elements
                .Where(e => e.ControlType == "text" && !string.IsNullOrWhiteSpace(e.Name) && e.Name.Length <= 60)
                .Select(e => e.Name.Trim()).Distinct().Take(60).ToList();
            if (texts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== 화면에 보이는 텍스트 ===");
                sb.AppendLine(string.Join(" | ", texts));
            }

            string xaml = PrismViewResolver.ReadForPrompt(page.ViewXamlPath, MaxXamlChars);
            if (xaml != null)
            {
                sb.AppendLine();
                sb.AppendLine($"=== View XAML ({System.IO.Path.GetFileName(page.ViewXamlPath)}) ===");
                sb.AppendLine(xaml);
            }

            string code = PrismViewResolver.ReadForPrompt(page.ViewCodePath, MaxCodeBehindChars);
            if (code != null && !PrismViewResolver.IsTrivialCodeBehind(code))
            {
                sb.AppendLine();
                sb.AppendLine($"=== View 코드비하인드 ({System.IO.Path.GetFileName(page.ViewCodePath)}) ===");
                sb.AppendLine(code);
            }

            string vm = PrismViewResolver.ReadForPrompt(page.ViewModelPath, MaxViewModelChars);
            if (vm != null)
            {
                sb.AppendLine();
                sb.AppendLine($"=== ViewModel ({System.IO.Path.GetFileName(page.ViewModelPath)}) ===");
                sb.AppendLine(vm);
            }

            if (xaml == null && vm == null)
            {
                sb.AppendLine();
                sb.AppendLine("(소스 코드를 찾지 못했습니다. 화면 정보만으로 보수적으로 작성하고, 불확실한 내용은 쓰지 마세요.)");
            }

            return sb.ToString();
        }

        // ─── Response ───────────────────────────────────────────────────────

        [DataContract] private sealed class LayoutDto    { [DataMember(Name = "area")] public string Area; [DataMember(Name = "description")] public string Description; }
        [DataContract] private sealed class ControlDto   { [DataMember(Name = "callout")] public int? Callout; [DataMember(Name = "name")] public string Name; [DataMember(Name = "description")] public string Description; }
        [DataContract] private sealed class ProcedureDto { [DataMember(Name = "title")] public string Title; [DataMember(Name = "steps")] public List<string> Steps; }

        [DataContract]
        private sealed class ResponseDto
        {
            [DataMember(Name = "summary")]    public string Summary;
            [DataMember(Name = "layout")]     public List<LayoutDto> Layout;
            [DataMember(Name = "controls")]   public List<ControlDto> Controls;
            [DataMember(Name = "procedures")] public List<ProcedureDto> Procedures;
            [DataMember(Name = "notes")]      public List<string> Notes;
        }

        public static void Apply(ManualPage page, string response)
        {
            var dto = TryParse(response);
            if (dto == null)
            {
                // Couldn't parse → keep raw text so nothing is lost; user can edit.
                page.Summary = response.Trim();
                return;
            }

            var summary = new StringBuilder((dto.Summary ?? "").Trim());
            if (dto.Layout != null && dto.Layout.Count > 0)
            {
                summary.AppendLine().AppendLine().AppendLine("[화면 구성]");
                foreach (var l in dto.Layout.Where(l => l != null))
                    summary.AppendLine($"- {l.Area}: {l.Description}");
            }
            page.Summary = summary.ToString().Trim();

            var controls = (dto.Controls ?? new List<ControlDto>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name + c.Description))
                .Select(c =>
                {
                    var matchedCo = c.Callout.HasValue ? page.Callouts.FirstOrDefault(p => p.Number == c.Callout.Value) : null;
                    if (matchedCo == null && !string.IsNullOrEmpty(c.Name))
                        matchedCo = page.Callouts.FirstOrDefault(p => string.Equals(p.Label, c.Name, StringComparison.OrdinalIgnoreCase));

                    return new ControlDesc
                    {
                        Callout     = matchedCo != null ? matchedCo.Number : (c.Callout.HasValue && page.Callouts.Any(p => p.Number == c.Callout.Value) ? c.Callout : null),
                        Name        = (c.Name ?? "").Trim(),
                        Description = (c.Description ?? "").Trim(),
                        ElementId   = matchedCo?.ElementId
                    };
                }).ToList();

            // Every callout should have a row (even if the LLM skipped it) so the user sees what's missing.
            foreach (var co in page.Callouts)
                if (!controls.Any(c => c.Callout == co.Number))
                    controls.Add(new ControlDesc { Callout = co.Number, Name = co.Label, Description = "", ElementId = co.ElementId });

            page.Controls = controls.OrderBy(c => c.Callout ?? int.MaxValue).ToList();

            var proc = new StringBuilder();
            foreach (var p in (dto.Procedures ?? new List<ProcedureDto>()).Where(p => p != null))
            {
                proc.AppendLine($"[{(p.Title ?? "절차").Trim()}]");
                var steps = p.Steps ?? new List<string>();
                for (int i = 0; i < steps.Count; i++)
                    proc.AppendLine($"{i + 1}. {steps[i]}");
                proc.AppendLine();
            }
            page.Procedures = proc.ToString().Trim();

            page.Notes = string.Join(Environment.NewLine,
                (dto.Notes ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => "- " + n.Trim()));
        }

        private static ResponseDto TryParse(string response)
        {
            string json = ExtractJson(response);
            if (json == null) return null;

            // LLMs often emit "callout": "3" / "①" / null — normalize to a number or null.
            json = Regex.Replace(json, @"""callout""\s*:\s*""\s*(\d+)\s*""", @"""callout"": $1");
            json = Regex.Replace(json, @"""callout""\s*:\s*""[^""]*""", @"""callout"": null");
            // notes as a single string → array
            json = Regex.Replace(json, @"""notes""\s*:\s*(""(?:[^""\\]|\\.)*"")", @"""notes"": [$1]");

            try
            {
                var ser = new DataContractJsonSerializer(typeof(ResponseDto));
                using (var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return ser.ReadObject(ms) as ResponseDto;
            }
            catch { return null; }
        }

        private static string ExtractJson(string text)
        {
            text = text.Trim();
            var fence = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```");
            if (fence.Success) text = fence.Groups[1].Value.Trim();
            int s = text.IndexOf('{'), e = text.LastIndexOf('}');
            return s >= 0 && e > s ? text.Substring(s, e - s + 1) : null;
        }
    }
}
