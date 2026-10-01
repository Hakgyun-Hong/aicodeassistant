using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;

namespace AIAssistant
{
    /// <summary>
    /// Analyzes VS Solution source files to extract:
    ///   1. XAML: AutomationId / x:Name / Command Binding per element
    ///   2. C#:   ViewModel Command property → Execute method → Call graph (Regex-based)
    ///
    /// Uses EnvDTE to enumerate solution files — no external Roslyn NuGet required.
    /// For deeper analysis, Roslyn Workspace APIs can be layered on top.
    ///
    /// Design note: keeps things intentionally simple (Regex parse) for V1.
    /// The accuracy is "best-effort" and produces useful code traces
    /// without requiring Roslyn symbol resolution.
    /// </summary>
    public sealed class SourceAnalyzer
    {
        // ─── XAML element descriptor ─────────────────────────────────────────

        public sealed class XamlElement
        {
            public string AutomationId    { get; set; }
            public string XName           { get; set; }
            public string ControlType     { get; set; }   // Button, TextBox...
            public string CommandBinding  { get; set; }   // e.g. "StartCommand"
            public string FilePath        { get; set; }
            public int    Line            { get; set; }
            public string Fragment        { get; set; }   // raw XAML snippet
        }

        // ─── Source index (built once per analysis session) ──────────────────

        private readonly List<XamlElement> _xamlIndex = new List<XamlElement>();
        private readonly List<string>      _csFiles   = new List<string>();
        private readonly string            _solutionDir;

        public SourceAnalyzer(string solutionDir)
        {
            _solutionDir = solutionDir;
        }

        /// <summary>
        /// Build XAML index and C# file list from solution directory.
        /// Call once before running correlations.
        /// </summary>
        public void BuildIndex()
        {
            _xamlIndex.Clear();
            _csFiles.Clear();

            if (string.IsNullOrEmpty(_solutionDir) || !Directory.Exists(_solutionDir))
                return;

            // Collect all .xaml and .cs files (excluding obj/bin)
            var allFiles = Directory.GetFiles(_solutionDir, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string lower = f.ToLower();
                    return !lower.Contains("\\obj\\") && !lower.Contains("\\bin\\") &&
                           !lower.Contains("/.git/");
                }).ToList();

            foreach (var f in allFiles)
            {
                string ext = Path.GetExtension(f).ToLower();
                if (ext == ".xaml") ParseXamlFile(f);
                if (ext == ".cs")   _csFiles.Add(f);
            }

            Log($"Index built: {_xamlIndex.Count} XAML elements, {_csFiles.Count} CS files.");
        }

        /// <summary>
        /// Try to find XAML source for an AutomationId or x:Name.
        /// </summary>
        public XamlElement FindByAutomationId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _xamlIndex.FirstOrDefault(x =>
                string.Equals(x.AutomationId, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.XName, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Build a SourceTrace for a given UI element, using XAML + C# analysis.
        /// </summary>
        public SourceTrace BuildTrace(UiElementInfo element)
        {
            var trace = new SourceTrace
            {
                ElementUniqueId = element.UniqueId,
                AutomationId    = element.AutomationId,
                XName           = element.XName
            };

            // Step 1: find XAML entry
            var xamlEl = FindByAutomationId(element.AutomationId)
                      ?? FindByAutomationId(element.Name);

            if (xamlEl == null) return trace;

            trace.XamlFilePath  = xamlEl.FilePath;
            trace.XamlLine      = xamlEl.Line;
            trace.XamlFragment  = xamlEl.Fragment;
            trace.BindingPath   = xamlEl.CommandBinding;
            trace.BindingType   = string.IsNullOrEmpty(xamlEl.CommandBinding) ? "Property" : "Command";
            trace.Evidence.Add($"{Path.GetFileName(xamlEl.FilePath)}:{xamlEl.Line}");

            if (string.IsNullOrEmpty(trace.BindingPath)) return trace;

            // Step 2: find Command property in ViewModels
            var vmResult = FindCommandInViewModels(trace.BindingPath);
            if (vmResult == null) return trace;

            trace.ViewModelTypeName   = vmResult.ClassName;
            trace.CommandPropertyName = trace.BindingPath;
            trace.Evidence.Add($"{Path.GetFileName(vmResult.FilePath)}:{vmResult.CommandLine}");

            // Step 3: locate Execute method
            if (!string.IsNullOrEmpty(vmResult.ExecuteMethod))
            {
                trace.ExecuteMethodName = vmResult.ExecuteMethod;
                var execResult = FindMethod(vmResult.FilePath, vmResult.ExecuteMethod);
                if (execResult.HasValue)
                    trace.Evidence.Add($"{Path.GetFileName(vmResult.FilePath)}:{execResult.Value}");

                // Step 4: Build call graph (1 level deep)
                var calls = ExtractMethodCalls(vmResult.FilePath, vmResult.ExecuteMethod);
                foreach (var call in calls)
                {
                    trace.CallGraph.Add(new CodeSymbol
                    {
                        Kind   = "Method",
                        Symbol = $"{vmResult.ClassName}.{call}",
                        File   = vmResult.FilePath
                    });
                }
            }

            // Step 5: CanExecute
            if (!string.IsNullOrEmpty(vmResult.CanExecuteMethod))
            {
                trace.CanExecuteMethodName = vmResult.CanExecuteMethod;
                trace.CanExecuteCondition  = ExtractCanExecuteCondition(vmResult.FilePath, vmResult.CanExecuteMethod);
                if (!string.IsNullOrEmpty(trace.CanExecuteCondition))
                    trace.Evidence.Add($"{Path.GetFileName(vmResult.FilePath)}: CanExecute → {trace.CanExecuteCondition}");
            }

            return trace;
        }

        // ─── XAML parsing ─────────────────────────────────────────────────────

        // Pattern: <Button ... x:Name="StartButton" ... Command="{Binding StartCommand}" ...
        // Handles multi-line XAML element definitions.
        private static readonly Regex RxXamlElement = new Regex(
            @"<(?<type>\w+)\s+(?:[^>]*?)(?:AutomationProperties\.AutomationId\s*=\s*[""'](?<aid>[^""']+)[""']|x:Name\s*=\s*[""'](?<xname>[^""']+)[""']|Command\s*=\s*[""{]\s*(?:Binding\s+)?(?<cmd>[^""}\s]+)\s*[}""])[^>]*(?:/>|>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex RxCommand = new Regex(
            @"Command\s*=\s*[""{\s]*(?:Binding\s+)?(?<cmd>[\w.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxAutomationId = new Regex(
            @"AutomationProperties\.AutomationId\s*=\s*[""'](?<id>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxXName = new Regex(
            @"x:Name\s*=\s*[""'](?<name>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void ParseXamlFile(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                var lines = content.Split('\n');

                // Find all control elements with name or command binding
                // We look for tags of known control types
                var controlTypes = new[] { "Button", "TextBox", "ComboBox", "CheckBox", "RadioButton",
                    "ListBox", "DataGrid", "TabItem", "MenuItem", "Slider", "PasswordBox",
                    "DatePicker", "TreeView", "ListView", "ToggleButton", "RepeatButton",
                    "Expander", "GroupBox", "TabControl", "TextBlock", "Label", "Hyperlink" };

                foreach (var ct in controlTypes)
                {
                    // Match opening tags of this type
                    var tagPattern = new Regex(
                        $@"<{ct}\b([^>]*(?:>|/>))",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    int searchFrom = 0;
                    Match m;
                    while ((m = tagPattern.Match(content, searchFrom)).Success)
                    {
                        string tagContent = m.Value;
                        int lineNum = content.Substring(0, m.Index).Count(c => c == '\n') + 1;

                        string aid   = RxAutomationId.Match(tagContent).Groups["id"].Value;
                        string xname = RxXName.Match(tagContent).Groups["name"].Value;
                        string cmd   = RxCommand.Match(tagContent).Groups["cmd"].Value;

                        // Only index elements that have at least one identifier or command
                        if (!string.IsNullOrEmpty(aid) || !string.IsNullOrEmpty(xname) ||
                            !string.IsNullOrEmpty(cmd))
                        {
                            // Clean fragment (first 200 chars)
                            string frag = tagContent.Length > 200
                                ? tagContent.Substring(0, 200).TrimEnd() + "..."
                                : tagContent.TrimEnd();

                            _xamlIndex.Add(new XamlElement
                            {
                                AutomationId   = aid,
                                XName          = xname,
                                ControlType    = ct,
                                CommandBinding = cmd,
                                FilePath       = filePath,
                                Line           = lineNum,
                                Fragment       = frag
                            });
                        }
                        searchFrom = m.Index + 1;
                    }
                }
            }
            catch (Exception ex) { Log($"XAML parse error {filePath}: {ex.Message}"); }
        }

        // ─── C# analysis ──────────────────────────────────────────────────────

        private sealed class ViewModelCommandResult
        {
            public string ClassName         { get; set; }
            public string FilePath          { get; set; }
            public int    CommandLine       { get; set; }
            public string ExecuteMethod     { get; set; }
            public string CanExecuteMethod  { get; set; }
        }

        // Matches:  public ICommand StartCommand { get; } = new DelegateCommand(ExecuteStart, CanExecuteStart);
        // Or:       StartCommand = new RelayCommand(ExecuteStart);
        private static readonly Regex RxCommandProp = new Regex(
            @"(?:public\s+\w+\s+)?(?<prop>\w+Command\w*)\s*[{=].*?new\s+\w*[Cc]ommand\w*\s*\(\s*(?<exec>\w+)(?:\s*,\s*(?<canexec>\w+))?",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Matches class name
        private static readonly Regex RxClassName = new Regex(
            @"(?:public|internal|private|sealed|abstract)\s+(?:partial\s+)?class\s+(?<name>\w+)",
            RegexOptions.Compiled);

        private ViewModelCommandResult FindCommandInViewModels(string commandName)
        {
            // Search only ViewModel files first (heuristic: name contains "ViewModel" or "Vm")
            var viewModelFiles = _csFiles
                .Where(f => f.ToLower().Contains("viewmodel") || f.ToLower().Contains("_vm"))
                .Concat(_csFiles) // then all CS files as fallback
                .Distinct()
                .ToList();

            foreach (var file in viewModelFiles)
            {
                try
                {
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    if (!content.Contains(commandName)) continue;

                    string className = RxClassName.Matches(content)
                        .Cast<Match>().LastOrDefault()?.Groups["name"].Value ?? "";

                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(commandName)) continue;
                        var m = RxCommandProp.Match(lines[i]);
                        if (!m.Success)
                        {
                            // Try multi-line match (next 3 lines)
                            string joined = string.Join(" ", lines.Skip(i).Take(4));
                            m = RxCommandProp.Match(joined);
                        }
                        if (!m.Success) continue;

                        return new ViewModelCommandResult
                        {
                            ClassName        = className,
                            FilePath         = file,
                            CommandLine      = i + 1,
                            ExecuteMethod    = m.Groups["exec"].Value,
                            CanExecuteMethod = m.Groups["canexec"].Value
                        };
                    }
                }
                catch { }
            }
            return null;
        }

        private int? FindMethod(string filePath, string methodName)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    // private void ExecuteStart() or private bool CanExecuteStart()
                    if (Regex.IsMatch(lines[i], $@"\b{Regex.Escape(methodName)}\s*\("))
                        return i + 1;
                }
            }
            catch { }
            return null;
        }

        private List<string> ExtractMethodCalls(string filePath, string methodName)
        {
            var calls = new List<string>();
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                // Find method body
                var methodStart = new Regex(
                    $@"(?:private|protected|public|internal)\s+\w+\s+{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{",
                    RegexOptions.Singleline);

                var m = methodStart.Match(content);
                if (!m.Success) return calls;

                // Extract body (find matching closing brace)
                int depth = 1, pos = m.Index + m.Length;
                int bodyStart = pos;
                while (pos < content.Length && depth > 0)
                {
                    if (content[pos] == '{') depth++;
                    if (content[pos] == '}') depth--;
                    pos++;
                }
                string body = content.Substring(bodyStart, pos - bodyStart - 1);

                // Find method/property calls: something.Method() or Method()
                var callPattern = new Regex(@"(?<obj>\w+)\.(?<method>\w+)\s*\(", RegexOptions.Compiled);
                foreach (Match cm in callPattern.Matches(body))
                {
                    string call = $"{cm.Groups["obj"].Value}.{cm.Groups["method"].Value}()";
                    if (!calls.Contains(call))
                        calls.Add(call);
                }
                calls = calls.Take(8).ToList(); // limit depth
            }
            catch { }
            return calls;
        }

        private string ExtractCanExecuteCondition(string filePath, string methodName)
        {
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                var methodStart = new Regex(
                    $@"(?:private|protected|public|internal)\s+bool\s+{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{([^}}]{{1,500}})\}}",
                    RegexOptions.Singleline);

                var m = methodStart.Match(content);
                if (!m.Success) return null;

                string body = m.Groups[1].Value.Trim();

                // Look for "return <condition>"
                var ret = Regex.Match(body, @"return\s+([^;]+);");
                if (ret.Success)
                {
                    string cond = ret.Groups[1].Value.Trim();
                    // Humanize common patterns
                    cond = cond
                        .Replace("&&", " AND ")
                        .Replace("||", " OR ")
                        .Replace("==", " is ")
                        .Replace("!=", " is not ")
                        .Replace("null", "null/not set");
                    return cond;
                }
            }
            catch { }
            return null;
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][SourceAnalyzer] {msg}");
    }
}
