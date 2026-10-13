using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIAssistant
{
    /// <summary>
    /// Resolves a Prism navigation name (UI XML &lt;ViewName&gt;) to View / ViewModel source files.
    ///
    /// Resolution order — View:
    ///   1. containerRegistry.RegisterForNavigation&lt;TView[, TViewModel]&gt;("Name" | nameof(X) | Const.Name)
    ///      (no argument → navigation name = TView class name)
    ///   2. A XAML whose x:Class short name == ViewName  (or ViewName + "View")
    ///   3. A XAML file named ViewName.xaml
    ///
    /// Resolution order — ViewModel:
    ///   1. Second generic arg of RegisterForNavigation
    ///   2. ViewModelLocationProvider.Register&lt;TView, TViewModel&gt;()
    ///   3. XAML DataContext element (&lt;UserControl.DataContext&gt;&lt;vm:XxxViewModel/&gt;)
    ///   4. Code-behind "DataContext = new XxxViewModel" / ctor-injected XxxViewModel
    ///   5. ViewModelLocator naming convention (XView → XViewModel, X_View → X_ViewModel, X → XViewModel)
    /// </summary>
    public sealed class PrismViewResolver
    {
        public sealed class Resolution
        {
            public string ViewType, XamlPath, CodePath, ViewModelType, ViewModelPath;
            public List<string> Notes = new List<string>();
        }

        private sealed class Registration
        {
            public string NavName, ViewType, ViewModelType, File;
        }

        private readonly string _root;
        private readonly Dictionary<string, string> _xamlByClass  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // short class → xaml
        private readonly Dictionary<string, string> _xamlByFile   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file name w/o ext → xaml
        private readonly Dictionary<string, string> _csByClass    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // short class → .cs (non code-behind preferred)
        private readonly Dictionary<string, string> _constants    = new Dictionary<string, string>(StringComparer.Ordinal);           // const name → value
        private readonly Dictionary<string, string> _vmLocator    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // view → vm
        private readonly List<Registration>         _registrations = new List<Registration>();

        private readonly List<string> _roots = new List<string>();

        public int XamlCount => _xamlByClass.Count;
        public int RegistrationCount => _registrations.Count;
        public string Root => _root;

        public PrismViewResolver(string sourceRoot, IEnumerable<string> additionalRoots = null)
        {
            _root = sourceRoot;
            if (!string.IsNullOrEmpty(sourceRoot) && Directory.Exists(sourceRoot))
                _roots.Add(sourceRoot);
            if (additionalRoots != null)
            {
                foreach (var r in additionalRoots)
                {
                    if (!string.IsNullOrEmpty(r) && Directory.Exists(r) && !_roots.Contains(r))
                        _roots.Add(r);
                }
            }
        }

        // ─── Index ──────────────────────────────────────────────────────────

        private static readonly Regex RxXClass = new Regex(@"x:Class\s*=\s*""(?<c>[\w.]+)""", RegexOptions.Compiled);
        private static readonly Regex RxClass  = new Regex(@"\b(?:class|record)\s+(?<c>\w+)", RegexOptions.Compiled);
        private static readonly Regex RxConst  = new Regex(@"\bconst\s+string\s+(?<n>\w+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex RxStaticStr = new Regex(@"\bstatic\s+readonly\s+string\s+(?<n>\w+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

        // RegisterForNavigation<V>() / <V, VM>("name") / RegisterTypeForNavigation<V>(nameof(V))
        private static readonly Regex RxRegisterNav = new Regex(
            @"Register(?:Type)?ForNavigation\s*<\s*(?<v>[\w.]+)\s*(?:,\s*(?<vm>[\w.]+)\s*)?>\s*\(\s*(?<arg>[^)]*)\)",
            RegexOptions.Compiled);

        // Register<object, V>("name")  (Unity/DryIoc style navigation registration)
        private static readonly Regex RxRegisterObject = new Regex(
            @"Register\s*<\s*object\s*,\s*(?<v>[\w.]+)\s*>\s*\(\s*(?<arg>[^)]*)\)",
            RegexOptions.Compiled);

        private static readonly Regex RxVmLocator = new Regex(
            @"ViewModelLocationProvider\s*\.\s*Register\s*<\s*(?<v>[\w.]+)\s*,\s*(?<vm>[\w.]+)\s*>",
            RegexOptions.Compiled);

        public void BuildIndex()
        {
            if (_roots.Count == 0) return;

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rootDir in _roots)
            {
                foreach (var file in EnumerateSourceFiles(rootDir))
                {
                    if (!seenFiles.Add(file)) continue;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    string text;
                    try { text = File.ReadAllText(file, Encoding.UTF8); }
                    catch { continue; }

                if (ext == ".xaml")
                {
                    var m = RxXClass.Match(text);
                    if (m.Success)
                    {
                        string shortName = ShortName(m.Groups["c"].Value);
                        if (!_xamlByClass.ContainsKey(shortName)) _xamlByClass[shortName] = file;
                    }
                    string fn = Path.GetFileNameWithoutExtension(file);
                    if (!_xamlByFile.ContainsKey(fn)) _xamlByFile[fn] = file;
                    continue;
                }

                // .cs
                bool isCodeBehind = file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase);
                bool isGenerated  = file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                                    file.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
                if (isGenerated) continue;

                foreach (Match cm in RxClass.Matches(text))
                {
                    string c = cm.Groups["c"].Value;
                    if (!_csByClass.ContainsKey(c) || (!isCodeBehind && _csByClass[c].EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)))
                        _csByClass[c] = file;
                }

                foreach (Match km in RxConst.Matches(text))
                    _constants[km.Groups["n"].Value] = km.Groups["v"].Value;
                foreach (Match km in RxStaticStr.Matches(text))
                    if (!_constants.ContainsKey(km.Groups["n"].Value))
                        _constants[km.Groups["n"].Value] = km.Groups["v"].Value;

                if (text.IndexOf("Navigation", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("Register<object", StringComparison.Ordinal) >= 0)
                {
                    foreach (Match rm in RxRegisterNav.Matches(text))
                        _registrations.Add(new Registration
                        {
                            ViewType      = ShortName(rm.Groups["v"].Value),
                            ViewModelType = rm.Groups["vm"].Success ? ShortName(rm.Groups["vm"].Value) : null,
                            NavName       = rm.Groups["arg"].Value.Trim(),   // resolved lazily (constants may be indexed later)
                            File          = file
                        });
                    foreach (Match rm in RxRegisterObject.Matches(text))
                        _registrations.Add(new Registration
                        {
                            ViewType = ShortName(rm.Groups["v"].Value),
                            NavName  = rm.Groups["arg"].Value.Trim(),
                            File     = file
                        });
                }

                foreach (Match lm in RxVmLocator.Matches(text))
                    _vmLocator[ShortName(lm.Groups["v"].Value)] = ShortName(lm.Groups["vm"].Value);
                }
            }

            // Resolve registration names now that all constants are known.
            foreach (var r in _registrations)
                r.NavName = ResolveNavNameArg(r.NavName, r.ViewType);

            Log($"Index: {_xamlByClass.Count} XAML views, {_csByClass.Count} classes, {_registrations.Count} Prism registrations.");
        }

        private string ResolveNavNameArg(string arg, string viewType)
        {
            if (string.IsNullOrWhiteSpace(arg)) return viewType;                 // RegisterForNavigation<V>()
            var s = Regex.Match(arg, @"^""(?<v>[^""]*)""");
            if (s.Success) return s.Groups["v"].Value;                           // "Name"
            var n = Regex.Match(arg, @"^nameof\s*\(\s*(?<v>[\w.]+)\s*\)");
            if (n.Success) return ShortName(n.Groups["v"].Value);                // nameof(X)
            string last = ShortName(arg.Trim());                                 // Const.Name / ViewNames.EFEM
            string val;
            return _constants.TryGetValue(last, out val) ? val : last;
        }

        // ─── Resolve ────────────────────────────────────────────────────────

        public Resolution Resolve(string viewName)
        {
            var r = new Resolution();
            if (string.IsNullOrWhiteSpace(viewName)) return r;
            viewName = viewName.Trim();

            // 1) Prism registration
            var reg = _registrations.FirstOrDefault(x => string.Equals(x.NavName, viewName, StringComparison.Ordinal))
                   ?? _registrations.FirstOrDefault(x => string.Equals(x.NavName, viewName, StringComparison.OrdinalIgnoreCase));
            if (reg != null)
            {
                r.ViewType = reg.ViewType;
                r.ViewModelType = reg.ViewModelType;
                r.Notes.Add($"Prism 등록: {Path.GetFileName(reg.File)} → {reg.ViewType}");
            }

            // 2/3) XAML by class or file name
            if (r.ViewType == null)
            {
                var viewCandidates = new List<string> { viewName };
                if (viewName.EndsWith("_View", StringComparison.OrdinalIgnoreCase))
                {
                    viewCandidates.Add(viewName.Substring(0, viewName.Length - 5));
                    viewCandidates.Add(viewName.Substring(0, viewName.Length - 5) + "View");
                }
                else if (viewName.EndsWith("View", StringComparison.OrdinalIgnoreCase))
                {
                    viewCandidates.Add(viewName.Substring(0, viewName.Length - 4));
                    viewCandidates.Add(viewName.Substring(0, viewName.Length - 4) + "_View");
                }
                else
                {
                    viewCandidates.Add(viewName + "View");
                    viewCandidates.Add(viewName + "_View");
                }

                foreach (var cand in viewCandidates)
                {
                    if (_xamlByClass.ContainsKey(cand)) { r.ViewType = cand; r.Notes.Add($"x:Class 일치: {cand}"); break; }
                }
            }

            string xaml = null;
            if (r.ViewType != null) _xamlByClass.TryGetValue(r.ViewType, out xaml);
            if (xaml == null)
            {
                var fileCandidates = new List<string>();
                if (r.ViewType != null) fileCandidates.Add(r.ViewType);
                fileCandidates.Add(viewName);
                if (viewName.EndsWith("_View", StringComparison.OrdinalIgnoreCase))
                {
                    fileCandidates.Add(viewName.Substring(0, viewName.Length - 5));
                    fileCandidates.Add(viewName.Substring(0, viewName.Length - 5) + "View");
                }
                else if (viewName.EndsWith("View", StringComparison.OrdinalIgnoreCase))
                {
                    fileCandidates.Add(viewName.Substring(0, viewName.Length - 4));
                }
                else
                {
                    fileCandidates.Add(viewName + "View");
                    fileCandidates.Add(viewName + "_View");
                }

                foreach (var cand in fileCandidates)
                {
                    if (cand != null && _xamlByFile.TryGetValue(cand, out xaml))
                    {
                        if (r.ViewType == null) r.ViewType = cand;
                        r.Notes.Add($"파일명 일치: {Path.GetFileName(xaml)}");
                        break;
                    }
                }
            }
            r.XamlPath = xaml;

            if (xaml != null && File.Exists(xaml + ".cs")) r.CodePath = xaml + ".cs";
            else if (r.ViewType != null && _csByClass.TryGetValue(r.ViewType, out var cs)) r.CodePath = cs;

            // ViewModel
            if (r.ViewModelType == null && r.ViewType != null && _vmLocator.TryGetValue(r.ViewType, out var locVm))
            {
                r.ViewModelType = locVm;
                r.Notes.Add("ViewModelLocationProvider.Register 일치");
            }
            if (r.ViewModelType == null && xaml != null)
            {
                r.ViewModelType = FindVmInXaml(xaml);
                if (r.ViewModelType != null) r.Notes.Add("XAML DataContext 일치");
            }
            if (r.ViewModelType == null && r.CodePath != null)
            {
                r.ViewModelType = FindVmInCodeBehind(r.CodePath);
                if (r.ViewModelType != null) r.Notes.Add("코드비하인드 참조 일치");
            }
            if (r.ViewModelType == null)
            {
                foreach (var cand in ConventionCandidates(r.ViewType ?? viewName))
                {
                    if (_csByClass.ContainsKey(cand)) { r.ViewModelType = cand; r.Notes.Add($"명명 규칙 일치: {cand}"); break; }
                }
            }
            if (r.ViewModelType != null) _csByClass.TryGetValue(r.ViewModelType, out r.ViewModelPath);

            if (r.XamlPath == null) r.Notes.Add("⚠ View XAML을 찾지 못했습니다");
            if (r.ViewModelPath == null) r.Notes.Add("⚠ ViewModel을 찾지 못했습니다");
            return r;
        }

        /// <summary>Apply resolution to a page unless the user locked a manual selection.</summary>
        public void ApplyTo(ManualPage page)
        {
            if (page.SourceLocked || !page.HasView) return;
            var r = Resolve(page.ViewName);
            page.ViewTypeName      = r.ViewType;
            page.ViewXamlPath      = r.XamlPath;
            page.ViewCodePath      = r.CodePath;
            page.ViewModelTypeName = r.ViewModelType;
            page.ViewModelPath     = r.ViewModelPath;
            page.SourceNote        = string.Join(" / ", r.Notes);
        }

        private static IEnumerable<string> ConventionCandidates(string view)
        {
            if (string.IsNullOrEmpty(view)) yield break;
            if (view.EndsWith("_View", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = view.Substring(0, view.Length - 5);
                yield return baseName + "_ViewModel";
                yield return baseName + "ViewModel";
                yield return baseName + "_VM";
                yield return baseName + "VM";
            }
            if (view.EndsWith("View", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = view.Substring(0, view.Length - 4);
                yield return view + "Model";              // EFEMView → EFEMViewModel
                yield return baseName + "ViewModel";
                yield return baseName + "_ViewModel";
                yield return baseName + "VM";
            }
            yield return view.Replace("View", "ViewModel");
            yield return view + "ViewModel";
            yield return view + "_ViewModel";
            yield return view + "VM";
        }

        private static readonly Regex RxXamlDataContext = new Regex(
            @"\.DataContext>\s*<(?:\w+:)?(?<vm>\w+)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex RxDesignContext = new Regex(
            @"d:DataContext\s*=\s*""\{d:DesignInstance\s+(?:Type=)?(?:\w+:)?(?<vm>\w+)", RegexOptions.Compiled);

        private static string FindVmInXaml(string xamlPath)
        {
            try
            {
                string text = File.ReadAllText(xamlPath);
                var m = RxXamlDataContext.Match(text);
                if (m.Success) return m.Groups["vm"].Value;
                m = RxDesignContext.Match(text);
                if (m.Success) return m.Groups["vm"].Value;
            }
            catch { }
            return null;
        }

        private static readonly Regex RxCodeBehindVm = new Regex(
            @"\b(?<vm>\w+(?:ViewModel|VM))\b", RegexOptions.Compiled);

        private string FindVmInCodeBehind(string codePath)
        {
            try
            {
                string text = File.ReadAllText(codePath);
                foreach (Match m in RxCodeBehindVm.Matches(text))
                {
                    string vm = m.Groups["vm"].Value;
                    if (vm != "ViewModel" && _csByClass.ContainsKey(vm)) return vm;
                }
            }
            catch { }
            return null;
        }

        // ─── Source text for LLM ────────────────────────────────────────────

        /// <summary>Read a source file, strip noise (usings, regions, blank runs), truncate.</summary>
        public static string ReadForPrompt(string path, int maxChars)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch { return null; }

            var sb = new StringBuilder();
            bool lastBlank = false;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                string t = raw.TrimEnd();
                string tt = t.TrimStart();
                if (tt.StartsWith("using ") && tt.EndsWith(";")) continue;
                if (tt.StartsWith("#region") || tt.StartsWith("#endregion")) continue;
                if (tt.Length == 0) { if (lastBlank) continue; lastBlank = true; }
                else lastBlank = false;
                sb.AppendLine(t);
            }
            string result = sb.ToString().Trim();
            if (result.Length > maxChars)
                result = result.Substring(0, maxChars) + "\n... (이하 생략)";
            return result;
        }

        /// <summary>True if a code-behind is just InitializeComponent boilerplate.</summary>
        public static bool IsTrivialCodeBehind(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string body = Regex.Replace(text, @"\s+", "");
            return body.Length < 260 && body.Contains("InitializeComponent()");
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        private static IEnumerable<string> EnumerateSourceFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string name = Path.GetFileName(dir).ToLowerInvariant();
                if (dir != root && (name == "bin" || name == "obj" || name == ".git" || name == ".vs" ||
                                    name == "packages" || name == "node_modules" || name == "testresults"))
                    continue;

                string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch { }

                foreach (var f in files)
                {
                    if (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        yield return f;
                }
                foreach (var d in dirs) stack.Push(d);
            }
        }

        private static string ShortName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            int i = typeName.LastIndexOf('.');
            return i >= 0 ? typeName.Substring(i + 1) : typeName;
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine($"[AIAssistant][PrismViewResolver] {msg}");
    }
}
