using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace AIAssistant
{
    /// <summary>
    /// Long-term HTML Export & Import manager for WPF Manual Projects.
    /// Exports a self-contained, interactive responsive web manual with embedded screenshots and metadata.
    /// Imports back from HTML to enable round-trip editing and continuous manual maintenance.
    /// </summary>
    public static class ManualHtmlManager
    {
        public const string FileFilter = "HTML Manual (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*";
        private const string MetadataScriptId = "manual-project-metadata";

        // ════════════════════════════════════════════════════════════════════════
        // EXPORT
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Exports the manual project as a standalone, interactive HTML document.
        /// Screenshots are embedded as Base64 Data URIs so the file can be shared independently.
        /// Project JSON is embedded in a &lt;script&gt; tag for 100% lossless re-import.
        /// </summary>
        public static void Export(ManualProject project, string outputPath, bool embedImages = true)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("출력 경로가 지정되지 않았습니다.", nameof(outputPath));

            string html = BuildHtml(project, outputPath, embedImages);
            File.WriteAllText(outputPath, html, Encoding.UTF8);
        }

        private static string BuildHtml(ManualProject project, string outputPath, bool embedImages)
        {
            var sb = new StringBuilder();
            var allPages = project.AllPages().Where(p => p.IsIncluded).ToList();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ko\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"  <title>{Escape(project.Title ?? "사용자 매뉴얼")}</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine(GetCss());
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body class=\"dark-theme\">");

            // Sidebar / Table of Contents
            sb.AppendLine("  <aside class=\"sidebar\" id=\"sidebar\">");
            sb.AppendLine("    <div class=\"sidebar-header\">");
            sb.AppendLine($"      <h2 class=\"app-title\">{Escape(project.AppName ?? "Application")}</h2>");
            sb.AppendLine($"      <p class=\"doc-title\">{Escape(project.Title ?? "User Manual")}</p>");
            sb.AppendLine("      <div class=\"search-box\">");
            sb.AppendLine("        <input type=\"text\" id=\"search-input\" placeholder=\"🔍 화면 / 기능 검색...\" />");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
            sb.AppendLine("    <nav class=\"toc-nav\" id=\"toc-nav\">");
            RenderToc(sb, project.Roots);
            sb.AppendLine("    </nav>");
            sb.AppendLine("  </aside>");

            // Main Content Area
            sb.AppendLine("  <main class=\"main-content\">");
            sb.AppendLine("    <header class=\"top-bar\">");
            sb.AppendLine("      <button class=\"btn-toggle-sidebar\" id=\"btn-toggle-sidebar\" title=\"사이드바 토글\">☰</button>");
            sb.AppendLine($"      <span class=\"top-title\">{Escape(project.Title)}</span>");
            sb.AppendLine("      <div class=\"top-actions\">");
            sb.AppendLine("        <button class=\"btn-action\" id=\"btn-theme\" title=\"테마 변경\">☀️ 라이트</button>");
            sb.AppendLine("        <button class=\"btn-action\" onclick=\"window.print()\" title=\"인쇄\">🖨️ 인쇄</button>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </header>");

            sb.AppendLine("    <div class=\"content-body\">");
            foreach (var page in allPages)
            {
                RenderPageSection(sb, page, outputPath, embedImages);
            }
            sb.AppendLine("    </div>");
            sb.AppendLine("  </main>");

            // Embed Project Metadata JSON for Lossless Re-Import
            string json = SerializeProject(project);
            sb.AppendLine($"  <script id=\"{MetadataScriptId}\" type=\"application/json\">");
            sb.AppendLine(json);
            sb.AppendLine("  </script>");

            // Interactive Client JavaScript
            sb.AppendLine("  <script>");
            sb.AppendLine(GetJs());
            sb.AppendLine("  </script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static void RenderToc(StringBuilder sb, List<ManualPage> roots)
        {
            if (roots == null || roots.Count == 0) return;
            sb.AppendLine("<ul class=\"toc-list\">");
            foreach (var root in roots)
                RenderTocItem(sb, root);
            sb.AppendLine("</ul>");
        }

        private static void RenderTocItem(StringBuilder sb, ManualPage page)
        {
            if (!page.IsIncluded) return;
            bool hasChildren = page.Children != null && page.Children.Any(c => c.IsIncluded);

            sb.AppendLine($"<li class=\"toc-item depth-{page.Depth}\" data-page-id=\"{page.Id}\">");
            string icon = hasChildren ? "📂 " : (page.HasScreenshot ? "🖼️ " : "📄 ");
            sb.AppendLine($"  <a href=\"#page-{page.Id}\" class=\"toc-link\">{icon}{Escape(page.DisplayName)}</a>");

            if (hasChildren)
            {
                sb.AppendLine("  <ul class=\"toc-sub-list\">");
                foreach (var child in page.Children)
                    RenderTocItem(sb, child);
                sb.AppendLine("  </ul>");
            }
            sb.AppendLine("</li>");
        }

        private static void RenderPageSection(StringBuilder sb, ManualPage page, string outputPath, bool embedImages)
        {
            sb.AppendLine($"<article class=\"page-section\" id=\"page-{page.Id}\" data-page-id=\"{page.Id}\">");

            // Breadcrumb
            if (page.NavPath != null && page.NavPath.Count > 0)
            {
                sb.AppendLine("  <div class=\"breadcrumb\">");
                sb.AppendLine($"    <span>{string.Join(" &rsaquo; ", page.NavPath.Select(Escape))}</span>");
                sb.AppendLine("  </div>");
            }

            // Title & Badges
            sb.AppendLine("  <div class=\"page-header\">");
            sb.AppendLine($"    <h1 class=\"page-title\">{Escape(page.DisplayName)}</h1>");
            sb.AppendLine("    <div class=\"badge-group\">");
            if (!string.IsNullOrEmpty(page.ViewName))
                sb.AppendLine($"      <span class=\"badge view-badge\" title=\"WPF View\">{Escape(page.ViewName)}</span>");
            if (page.IsHome)
                sb.AppendLine("      <span class=\"badge home-badge\">홈 화면</span>");
            sb.AppendLine($"      <span class=\"badge level-badge\">상세도: {page.DetailLevel}</span>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");

            // Screenshot with Interactive Callout Pins
            if (page.HasScreenshot && File.Exists(page.ScreenshotPath))
            {
                sb.AppendLine("  <div class=\"screenshot-wrapper\">");
                sb.AppendLine("    <div class=\"screenshot-container\" id=\"container-" + page.Id + "\">");

                string imgSrc;
                if (embedImages)
                {
                    byte[] bytes = File.ReadAllBytes(page.ScreenshotPath);
                    string b64 = Convert.ToBase64String(bytes);
                    imgSrc = $"data:image/png;base64,{b64}";
                }
                else
                {
                    // Relative path
                    imgSrc = MakeRelativePath(outputPath, page.ScreenshotPath);
                }

                sb.AppendLine($"      <img src=\"{imgSrc}\" alt=\"{Escape(page.DisplayName)}\" class=\"manual-img\" id=\"img-{page.Id}\" />");

                // Overlay Callout Pins (percentage positioning)
                double winW = page.WindowRect.Width > 0 ? page.WindowRect.Width : 1;
                double winH = page.WindowRect.Height > 0 ? page.WindowRect.Height : 1;
                double winX = page.WindowRect.X;
                double winY = page.WindowRect.Y;

                foreach (var co in page.Callouts.OrderBy(c => c.Number))
                {
                    double leftPct = winW > 1 ? ((co.X - winX) / winW) * 100.0 : 0;
                    double topPct  = winH > 1 ? ((co.Y - winY) / winH) * 100.0 : 0;
                    double wPct    = winW > 1 ? (co.W / winW) * 100.0 : 0;
                    double hPct    = winH > 1 ? (co.H / winH) * 100.0 : 0;

                    // Bounds check
                    leftPct = Math.Max(0, Math.Min(98, leftPct));
                    topPct  = Math.Max(0, Math.Min(98, topPct));

                    sb.AppendLine($"      <div class=\"callout-pin\" data-callout=\"{co.Number}\" data-page-id=\"{page.Id}\" " +
                                  $"style=\"left:{leftPct:F2}%; top:{topPct:F2}%;\" title=\"{co.Number}. {Escape(co.Label)}\">");
                    sb.AppendLine($"        <span class=\"pin-number\">{co.Number}</span>");
                    sb.AppendLine("      </div>");

                    // Bounding Box Highlight
                    if (wPct > 0.5 && hPct > 0.5)
                    {
                        sb.AppendLine($"      <div class=\"callout-box\" data-callout=\"{co.Number}\" data-page-id=\"{page.Id}\" " +
                                      $"style=\"left:{leftPct:F2}%; top:{topPct:F2}%; width:{wPct:F2}%; height:{hPct:F2}%;\"></div>");
                    }
                }

                sb.AppendLine("    </div>");
                sb.AppendLine($"    <p class=\"caption\">[그림] {Escape(page.DisplayName)} 화면 구성 및 주요 조작부</p>");
                sb.AppendLine("  </div>");
            }

            // Summary
            if (!string.IsNullOrWhiteSpace(page.Summary))
            {
                sb.AppendLine("  <section class=\"section-block summary-block\">");
                sb.AppendLine("    <h2 class=\"section-title\">📝 화면 개요</h2>");
                sb.AppendLine($"    <p class=\"summary-text\">{FormatMultiline(page.Summary)}</p>");
                sb.AppendLine("  </section>");
            }

            // Controls Table
            if (page.Controls != null && page.Controls.Count > 0)
            {
                sb.AppendLine("  <section class=\"section-block controls-block\">");
                sb.AppendLine("    <h2 class=\"section-title\">🎛️ 주요 컨트롤 및 조작 설명</h2>");
                sb.AppendLine("    <table class=\"controls-table\">");
                sb.AppendLine("      <thead>");
                sb.AppendLine("        <tr>");
                sb.AppendLine("          <th class=\"col-num\">번호</th>");
                sb.AppendLine("          <th class=\"col-name\">컨트롤 이름</th>");
                sb.AppendLine("          <th class=\"col-desc\">기능 및 사용 조건</th>");
                sb.AppendLine("        </tr>");
                sb.AppendLine("      </thead>");
                sb.AppendLine("      <tbody>");
                foreach (var ctrl in page.Controls)
                {
                    string calloutStr = ctrl.Callout.HasValue ? ctrl.Callout.Value.ToString() : "-";
                    string dataCallout = ctrl.Callout.HasValue ? $"data-callout=\"{ctrl.Callout.Value}\"" : "";
                    sb.AppendLine($"        <tr class=\"ctrl-row\" {dataCallout} data-page-id=\"{page.Id}\">");
                    sb.AppendLine($"          <td class=\"col-num\"><span class=\"badge-num\">{calloutStr}</span></td>");
                    sb.AppendLine($"          <td class=\"col-name\"><strong>{Escape(ctrl.Name)}</strong></td>");
                    sb.AppendLine($"          <td class=\"col-desc\">{FormatMultiline(ctrl.Description)}</td>");
                    sb.AppendLine("        </tr>");
                }
                sb.AppendLine("      </tbody>");
                sb.AppendLine("    </table>");
                sb.AppendLine("  </section>");
            }

            // Procedures
            if (!string.IsNullOrWhiteSpace(page.Procedures))
            {
                sb.AppendLine("  <section class=\"section-block procedures-block\">");
                sb.AppendLine("    <h2 class=\"section-title\">📋 조작 및 업무 절차</h2>");
                sb.AppendLine("    <div class=\"procedure-card\">");
                sb.AppendLine($"      <div class=\"proc-text\">{FormatMultiline(page.Procedures)}</div>");
                sb.AppendLine("    </div>");
                sb.AppendLine("  </section>");
            }

            // Notes
            if (!string.IsNullOrWhiteSpace(page.Notes))
            {
                sb.AppendLine("  <section class=\"section-block notes-block\">");
                sb.AppendLine("    <h2 class=\"section-title\">⚠️ 주의사항 및 확인사항</h2>");
                sb.AppendLine("    <div class=\"notes-box\">");
                sb.AppendLine($"      <div class=\"notes-text\">{FormatMultiline(page.Notes)}</div>");
                sb.AppendLine("    </div>");
                sb.AppendLine("  </section>");
            }

            sb.AppendLine("</article>");
        }

        // ════════════════════════════════════════════════════════════════════════
        // IMPORT
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Imports and restores a ManualProject from an HTML manual file.
        /// Extracts embedded project metadata and restores screenshots into the asset directory.
        /// </summary>
        public static ManualProject Import(string htmlFilePath, string customAssetsDir = null)
        {
            if (!File.Exists(htmlFilePath))
                throw new FileNotFoundException("HTML 매뉴얼 파일을 찾을 수 없습니다.", htmlFilePath);

            string html = File.ReadAllText(htmlFilePath, Encoding.UTF8);

            // 1. Try Primary Strategy: Embedded Metadata Script
            string marker = $"<script id=\"{MetadataScriptId}\" type=\"application/json\">";
            int idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + marker.Length;
                int end = html.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    string json = html.Substring(start, end - start).Trim();
                    var proj = DeserializeProject(json);
                    if (proj != null)
                    {
                        proj.ProjectPath = htmlFilePath;
                        RestoreImagesFromHtml(proj, html, htmlFilePath, customAssetsDir);
                        return proj;
                    }
                }
            }

            // 2. Fallback: Parse HTML DOM structure if metadata is missing
            return FallbackImportFromDom(html, htmlFilePath, customAssetsDir);
        }

        private static void RestoreImagesFromHtml(ManualProject project, string html, string htmlPath, string customAssetsDir)
        {
            string assetsDir = customAssetsDir ?? ManualProjectStore.AssetsDirFor(htmlPath);
            Directory.CreateDirectory(assetsDir);

            foreach (var page in project.AllPages())
            {
                // If screenshot exists on disk already, keep it
                if (!string.IsNullOrEmpty(page.ScreenshotPath) && File.Exists(page.ScreenshotPath))
                    continue;

                // Search for base64 image in HTML for this page
                string pattern = $@"id=[""']img-{Regex.Escape(page.Id)}[""'][^>]*src=[""']data:image/png;base64,([^""']+)[""']";
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(m.Groups[1].Value);
                        string dest = Path.Combine(assetsDir, $"{page.Id}.png");
                        File.WriteAllBytes(dest, bytes);
                        page.ScreenshotPath = dest;
                    }
                    catch { }
                }
            }
        }

        private static ManualProject FallbackImportFromDom(string html, string htmlPath, string customAssetsDir)
        {
            var project = new ManualProject
            {
                Title = ExtractBetween(html, "<title>", "</title>") ?? "Imported Manual",
                AppName = ExtractBetween(html, "<h2 class=\"app-title\">", "</h2>") ?? "App",
                ProjectPath = htmlPath,
                Roots = new List<ManualPage>()
            };

            string assetsDir = customAssetsDir ?? ManualProjectStore.AssetsDirFor(htmlPath);
            Directory.CreateDirectory(assetsDir);

            // Find all <article class="page-section" id="page-...">
            var matches = Regex.Matches(html, @"<article\s+class=[""']page-section[""']\s+id=[""']page-([^""']+)[""'](?:\s+data-page-id=[""']([^""']+)[""'])?[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string id = match.Groups[1].Value;
                string body = match.Groups[3].Value;

                var page = new ManualPage
                {
                    Id = id,
                    DisplayName = ExtractBetween(body, "<h1 class=\"page-title\">", "</h1>") ?? id,
                    Summary = ExtractBetween(body, "<p class=\"summary-text\">", "</p>") ?? "",
                    ViewName = ExtractBetween(body, "<span class=\"badge view-badge\"[^>]*>", "</span>") ?? "",
                    Controls = new List<ControlDesc>(),
                    Procedures = ExtractBetween(body, "<div class=\"proc-text\">", "</div>") ?? "",
                    Notes = ExtractBetween(body, "<div class=\"notes-text\">", "</div>") ?? ""
                };

                // Extract image
                var imgMatch = Regex.Match(body, @"src=[""']data:image/png;base64,([^""']+)[""']", RegexOptions.IgnoreCase);
                if (imgMatch.Success)
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(imgMatch.Groups[1].Value);
                        string dest = Path.Combine(assetsDir, $"{page.Id}.png");
                        File.WriteAllBytes(dest, bytes);
                        page.ScreenshotPath = dest;
                    }
                    catch { }
                }

                project.Roots.Add(page);
            }

            return project;
        }

        // ════════════════════════════════════════════════════════════════════════
        // SERIALIZATION HELPERS
        // ════════════════════════════════════════════════════════════════════════

        public static string SerializeProject(ManualProject project)
        {
            var ser = new DataContractJsonSerializer(typeof(ManualProject));
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, true, true))
                {
                    ser.WriteObject(writer, project);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static ManualProject DeserializeProject(string json)
        {
            var ser = new DataContractJsonSerializer(typeof(ManualProject));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (ManualProject)ser.ReadObject(ms);
            }
        }

        private static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        private static string FormatMultiline(string s) =>
            Escape(s ?? "").Replace("\r\n", "<br/>").Replace("\n", "<br/>");

        private static string MakeRelativePath(string fromPath, string toPath)
        {
            try
            {
                var fromUri = new Uri(Path.GetFullPath(fromPath));
                var toUri = new Uri(Path.GetFullPath(toPath));
                return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()).Replace('/', '\\');
            }
            catch { return toPath; }
        }

        private static string ExtractBetween(string source, string startTag, string endTag)
        {
            int s = source.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (s < 0) return null;
            s += startTag.Length;
            int e = source.IndexOf(endTag, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) return null;
            return source.Substring(s, e - s).Trim();
        }

        // ════════════════════════════════════════════════════════════════════════
        // CSS STYLESHEET (Modern, Responsive, Dark/Light, Print-Ready)
        // ════════════════════════════════════════════════════════════════════════

        private static string GetCss() => @"
:root {
  --bg-primary: #12151c;
  --bg-secondary: #1a1e27;
  --bg-card: #202634;
  --bg-card-hover: #272e3f;
  --text-primary: #e6edf3;
  --text-muted: #8b949e;
  --accent: #58a6ff;
  --accent-hover: #79b8ff;
  --accent-glow: rgba(88, 166, 255, 0.25);
  --border: #30363d;
  --pin-bg: linear-gradient(135deg, #1f6feb, #388bfd);
  --pin-border: #ffffff;
  --callout-box: rgba(88, 166, 255, 0.4);
  --highlight-row: rgba(88, 166, 255, 0.18);
  --sidebar-w: 280px;
}

body.light-theme {
  --bg-primary: #f6f8fa;
  --bg-secondary: #ffffff;
  --bg-card: #ffffff;
  --bg-card-hover: #f1f3f5;
  --text-primary: #1f2328;
  --text-muted: #656d76;
  --accent: #0969da;
  --accent-hover: #218bff;
  --accent-glow: rgba(9, 105, 218, 0.15);
  --border: #d0d7de;
  --pin-bg: linear-gradient(135deg, #0969da, #218bff);
  --pin-border: #ffffff;
  --callout-box: rgba(9, 105, 218, 0.35);
  --highlight-row: rgba(9, 105, 218, 0.12);
}

* { box-sizing: border-box; margin: 0; padding: 0; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
  background-color: var(--bg-primary);
  color: var(--text-primary);
  display: flex;
  min-height: 100vh;
  line-height: 1.6;
}

/* Sidebar */
.sidebar {
  width: var(--sidebar-w);
  background-color: var(--bg-secondary);
  border-right: 1px solid var(--border);
  height: 100vh;
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  transition: transform 0.3s ease;
  z-index: 100;
}
.sidebar-header {
  padding: 18px 16px 12px;
  border-bottom: 1px solid var(--border);
}
.app-title { font-size: 1.15rem; font-weight: 700; color: var(--accent); }
.doc-title { font-size: 0.85rem; color: var(--text-muted); margin-top: 2px; }
.search-box { margin-top: 10px; }
.search-box input {
  width: 100%;
  padding: 7px 12px;
  background-color: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 0.85rem;
  outline: none;
}
.search-box input:focus { border-color: var(--accent); }

.toc-nav {
  flex: 1;
  overflow-y: auto;
  padding: 12px 8px;
}
.toc-list, .toc-sub-list { list-style: none; }
.toc-sub-list { padding-left: 14px; margin-top: 2px; border-left: 1px solid var(--border); }
.toc-item { margin: 2px 0; }
.toc-link {
  display: block;
  padding: 6px 10px;
  border-radius: 5px;
  color: var(--text-primary);
  text-decoration: none;
  font-size: 0.88rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  transition: background 0.15s;
}
.toc-link:hover { background-color: var(--bg-card-hover); color: var(--accent); }

/* Main Content */
.main-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.top-bar {
  position: sticky;
  top: 0;
  height: 52px;
  background-color: var(--bg-secondary);
  border-bottom: 1px solid var(--border);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  z-index: 90;
}
.btn-toggle-sidebar {
  background: none; border: none; color: var(--text-primary); font-size: 1.3rem;
  cursor: pointer; display: none; margin-right: 12px;
}
.top-title { font-weight: 600; font-size: 0.95rem; }
.top-actions { display: flex; gap: 8px; }
.btn-action {
  background-color: var(--bg-card);
  border: 1px solid var(--border);
  color: var(--text-primary);
  padding: 5px 12px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 0.82rem;
  transition: all 0.2s;
}
.btn-action:hover { border-color: var(--accent); color: var(--accent); }

.content-body {
  max-width: 1040px;
  margin: 0 auto;
  width: 100%;
  padding: 32px 24px 80px;
}

/* Page Section */
.page-section {
  background-color: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 28px;
  margin-bottom: 40px;
  scroll-margin-top: 70px;
  box-shadow: 0 4px 12px rgba(0,0,0,0.1);
}
.breadcrumb { font-size: 0.82rem; color: var(--text-muted); margin-bottom: 8px; }
.page-header { display: flex; align-items: baseline; justify-content: space-between; flex-wrap: wrap; gap: 10px; margin-bottom: 20px; }
.page-title { font-size: 1.6rem; font-weight: 700; color: var(--text-primary); }
.badge-group { display: flex; gap: 6px; flex-wrap: wrap; }
.badge {
  font-size: 0.75rem; padding: 3px 8px; border-radius: 4px; font-weight: 500;
  background-color: var(--bg-secondary); border: 1px solid var(--border);
}
.view-badge { color: var(--accent); border-color: var(--accent); }
.home-badge { background-color: #238636; color: white; border: none; }
.level-badge { color: var(--text-muted); }

/* Screenshot & Interactive Pins */
.screenshot-wrapper {
  margin: 20px 0 28px;
  text-align: center;
}
.screenshot-container {
  position: relative;
  display: inline-block;
  max-width: 100%;
  border-radius: 8px;
  overflow: hidden;
  box-shadow: 0 4px 16px rgba(0,0,0,0.3);
  border: 1px solid var(--border);
}
.manual-img {
  display: block;
  max-width: 100%;
  height: auto;
}
.callout-pin {
  position: absolute;
  width: 24px;
  height: 24px;
  transform: translate(-50%, -50%);
  background: var(--pin-bg);
  border: 2px solid var(--pin-border);
  border-radius: 50%;
  box-shadow: 0 2px 8px rgba(0,0,0,0.4);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  color: white;
  font-size: 0.75rem;
  font-weight: 700;
  z-index: 10;
  transition: transform 0.2s, box-shadow 0.2s;
}
.callout-pin:hover, .callout-pin.active {
  transform: translate(-50%, -50%) scale(1.3);
  box-shadow: 0 0 12px var(--accent);
  z-index: 20;
}
.callout-box {
  position: absolute;
  border: 2px dashed var(--callout-box);
  background-color: rgba(88, 166, 255, 0.08);
  pointer-events: none;
  border-radius: 4px;
  display: none;
  z-index: 5;
}
.callout-box.active { display: block; }
.caption { font-size: 0.85rem; color: var(--text-muted); margin-top: 10px; font-weight: 500; }

/* Sections */
.section-block { margin-top: 24px; }
.section-title { font-size: 1.1rem; font-weight: 600; color: var(--accent); margin-bottom: 10px; border-bottom: 1px solid var(--border); padding-bottom: 6px; }
.summary-text { font-size: 0.95rem; color: var(--text-primary); }
.layout-list { padding-left: 20px; font-size: 0.92rem; }
.layout-list li { margin: 4px 0; }

/* Table */
.controls-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9rem;
  margin-top: 10px;
}
.controls-table th, .controls-table td {
  border: 1px solid var(--border);
  padding: 10px 12px;
  text-align: left;
}
.controls-table th { background-color: var(--bg-secondary); font-weight: 600; }
.col-num { width: 55px; text-align: center; }
.col-name { width: 220px; }
.badge-num {
  display: inline-block;
  width: 22px; height: 22px;
  border-radius: 50%;
  background: var(--pin-bg);
  color: white;
  text-align: center;
  line-height: 22px;
  font-size: 0.75rem;
  font-weight: 700;
}
.ctrl-row { transition: background 0.15s; cursor: pointer; }
.ctrl-row:hover, .ctrl-row.active { background-color: var(--highlight-row); }

/* Procedures & Notes */
.procedure-card {
  background-color: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 14px 18px;
  margin-bottom: 12px;
}
.proc-title { font-size: 0.98rem; font-weight: 600; color: var(--text-primary); margin-bottom: 8px; }
.proc-steps { padding-left: 22px; font-size: 0.9rem; }
.proc-steps li { margin: 4px 0; }

.notes-box {
  background-color: rgba(227, 179, 65, 0.1);
  border: 1px solid rgba(227, 179, 65, 0.4);
  border-radius: 6px;
  padding: 14px 18px;
  color: var(--text-primary);
}
.notes-box ul { padding-left: 20px; font-size: 0.9rem; }
.notes-box li { margin: 4px 0; }

/* Responsive & Print */
@media (max-width: 900px) {
  .btn-toggle-sidebar { display: inline-block; }
  .sidebar {
    position: fixed; left: 0; top: 0; bottom: 0;
    transform: translateX(-100%);
  }
  .sidebar.open { transform: translateX(0); }
}

@media print {
  .sidebar, .top-bar, .search-box, .btn-action { display: none !important; }
  body { background: white !important; color: black !important; }
  .page-section {
    border: none !important; box-shadow: none !important;
    page-break-after: always; padding: 0 !important; margin: 0 0 30px !important;
  }
}
";

        // ════════════════════════════════════════════════════════════════════════
        // JAVASCRIPT INTERACTIONS
        // ════════════════════════════════════════════════════════════════════════

        private static string GetJs() => @"
// Theme Toggle
const btnTheme = document.getElementById('btn-theme');
btnTheme.addEventListener('click', () => {
  document.body.classList.toggle('light-theme');
  const isLight = document.body.classList.contains('light-theme');
  btnTheme.textContent = isLight ? '🌙 다크' : '☀️ 라이트';
});

// Sidebar Toggle (Mobile)
const btnToggle = document.getElementById('btn-toggle-sidebar');
const sidebar = document.getElementById('sidebar');
if (btnToggle && sidebar) {
  btnToggle.addEventListener('click', () => sidebar.classList.toggle('open'));
}

// Search Filter
const searchInput = document.getElementById('search-input');
if (searchInput) {
  searchInput.addEventListener('input', (e) => {
    const q = e.target.value.toLowerCase().trim();
    document.querySelectorAll('.toc-item').forEach(item => {
      const txt = item.textContent.toLowerCase();
      item.style.display = (!q || txt.includes(q)) ? '' : 'none';
    });
  });
}

// Callout Pin & Table Row Linking
document.querySelectorAll('.callout-pin').forEach(pin => {
  const num = pin.dataset.callout;
  const pageId = pin.dataset.pageId;

  pin.addEventListener('mouseenter', () => highlight(pageId, num, true));
  pin.addEventListener('mouseleave', () => highlight(pageId, num, false));
  pin.addEventListener('click', () => {
    const row = document.querySelector(`.ctrl-row[data-page-id='${pageId}'][data-callout='${num}']`);
    if (row) row.scrollIntoView({ behavior: 'smooth', block: 'center' });
  });
});

document.querySelectorAll('.ctrl-row[data-callout]').forEach(row => {
  const num = row.dataset.callout;
  const pageId = row.dataset.pageId;

  row.addEventListener('mouseenter', () => highlight(pageId, num, true));
  row.addEventListener('mouseleave', () => highlight(pageId, num, false));
});

function highlight(pageId, num, active) {
  const pin = document.querySelector(`.callout-pin[data-page-id='${pageId}'][data-callout='${num}']`);
  const box = document.querySelector(`.callout-box[data-page-id='${pageId}'][data-callout='${num}']`);
  const row = document.querySelector(`.ctrl-row[data-page-id='${pageId}'][data-callout='${num}']`);

  if (pin) pin.classList.toggle('active', active);
  if (box) box.classList.toggle('active', active);
  if (row) row.classList.toggle('active', active);
}
";
    }
}
