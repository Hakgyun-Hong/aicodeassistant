using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Xml;

namespace AIAssistant
{
    /// <summary>
    /// Exports a ManualDocument or ManualProject to high-quality DOCX (Office Open XML).
    ///
    /// Uses System.IO.Packaging (WindowsBase.dll) + standard Open XML generation.
    /// No external NuGet dependencies required.
    ///
    /// Features:
    ///   - Elegant Cover Page with title, application info, and metadata summary card
    ///   - Table of Contents / Screen Index table
    ///   - Section-per-screen layout with clean page breaks (eliminates awkward page splits)
    ///   - Auto-expanding line heights (w:lineRule="auto") preventing screenshot clipping
    ///   - Aspect-ratio preserving downscaling capped to page boundaries (MAX_W = 15.0cm, MAX_H = 10.0cm)
    ///   - Subtle slate outline frame around screenshots
    ///   - Modern table styling: deep navy header (#1E3A8A), cell padding, zebra rows (#F8FAFC), circled numbers ①..⑳
    ///   - Styled Warning Callout boxes (#FFFBEB / #F59E0B) and Procedure step boxes (#F8FAFC / #3B82F6)
    ///   - Complete Korean typography support (맑은 고딕 / Segoe UI doc defaults)
    /// </summary>
    public static class ManualExporter
    {
        // ─── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Export a view-centric ManualProject. Pages follow the UI XML menu tree:
        ///   Cover → Screen Index → Heading (by depth) → annotated screenshot → 개요 → 컨트롤 표 → 사용 절차 → 주의사항
        /// </summary>
        public static void ExportProject(ManualProject project, string outputPath, string tempDir)
        {
            ExportDocx(BuildDocument(project, tempDir), outputPath);
        }

        public static ManualDocument BuildDocument(ManualProject project, string tempDir)
        {
            var doc = new ManualDocument
            {
                Title           = string.IsNullOrWhiteSpace(project.Title) ? $"{project.AppName} 사용자 매뉴얼" : project.Title,
                ApplicationName = project.AppName,
                Subtitle        = $"작성일: {DateTime.Now:yyyy-MM-dd}" +
                                  (string.IsNullOrEmpty(project.AppName) ? "" : $"  |  대상: {project.AppName}"),
                CapturedAt      = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            int order = 0;
            foreach (var root in project.Roots)
                AddPageBlocks(doc, root, project, tempDir, ref order);
            return doc;
        }

        private static void AddPageBlocks(ManualDocument doc, ManualPage page, ManualProject project, string tempDir, ref int order)
        {
            if (!page.IsIncluded) return;

            // Ensure callouts and controls are 100% synchronized before exporting
            page.SyncCalloutsFromControls();

            // Heading block (starts on a new page)
            doc.Blocks.Add(new ManualBlock
            {
                Type = ManualBlockType.Heading,
                Title = page.DisplayName + (string.IsNullOrWhiteSpace(page.ViewName) ? "" : $" ({page.ViewName})"),
                HeadingLevel = Math.Min(3, page.Depth + 1),
                Order = order++,
                PageBreakBefore = true,
                Content = page.PathText != page.DisplayName ? page.PathText : ""
            });

            if (page.HasScreenshot)
            {
                string img = page.ScreenshotPath;
                bool includeCallouts = project?.WordIncludeCallouts ?? false;
                if (includeCallouts && page.ShowCallouts && page.Callouts.Count > 0)
                {
                    try
                    {
                        img = ScreenCaptureEngine.AnnotateScreenshot(page.ScreenshotPath, page.CalloutItems(),
                            page.WindowRect, tempDir, $"export_{page.Id}");
                    }
                    catch
                    {
                        img = page.ScreenshotPath;
                    }
                }
                doc.Blocks.Add(new ManualBlock
                {
                    Type = ManualBlockType.Screenshot,
                    Title = $"[그림] {page.DisplayName} 화면 구성 및 컨트롤",
                    ScreenshotPath = img,
                    Order = order++
                });
            }

            if (!string.IsNullOrWhiteSpace(page.Summary))
                doc.Blocks.Add(new ManualBlock { Type = ManualBlockType.Description, Title = "화면 개요", Content = page.Summary, Order = order++ });

            var rows = page.Controls.Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Description)).ToList();
            if (rows.Count > 0)
            {
                var table = new ManualBlock { Type = ManualBlockType.Table, Title = "화면 구성 요소 및 기능 설명", Order = order++ };
                table.TableRows.Add(new[] { "번호", "컨트롤 / 항목", "기능 및 사용 조건" });
                int rowIdx = 1;
                foreach (var c in rows)
                {
                    string numStr = c.Callout.HasValue ? GetCircleNumber(c.Callout.Value) : (rowIdx++).ToString();
                    table.TableRows.Add(new[] { numStr, c.Name ?? "", c.Description ?? "" });
                }
                doc.Blocks.Add(table);
            }

            if (!string.IsNullOrWhiteSpace(page.Procedures))
                doc.Blocks.Add(new ManualBlock { Type = ManualBlockType.Procedure, Title = "주요 작업 절차", Content = page.Procedures, Order = order++ });

            if (!string.IsNullOrWhiteSpace(page.Notes))
                doc.Blocks.Add(new ManualBlock { Type = ManualBlockType.Warning, Title = "주의 및 확인사항", Content = page.Notes, Order = order++ });

            foreach (var child in page.Children)
                AddPageBlocks(doc, child, project, tempDir, ref order);
        }

        public static void ExportDocx(ManualDocument doc, string outputPath)
        {
            using (var pkg = Package.Open(outputPath, FileMode.Create, FileAccess.ReadWrite))
            {
                // Core relationships
                CreateRels(pkg);

                // Styles part
                var stylesPart = CreatePart(pkg, "/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
                WriteXml(stylesPart, BuildStylesXml());

                // Media images
                var imageRels = new Dictionary<string, string>(); // elementId → rId
                _imageSizes.Clear();
                int imgIdx = 0;

                var screenshotBlocks = doc.Blocks
                    .Where(b => b.Type == ManualBlockType.Screenshot &&
                                !string.IsNullOrEmpty(b.ScreenshotPath) &&
                                File.Exists(b.ScreenshotPath))
                    .ToList();

                foreach (var sb in screenshotBlocks)
                {
                    string rId = $"rId{++imgIdx}";
                    string imgPartUri = $"/word/media/img_{imgIdx}.png";
                    var imgPart = pkg.CreatePart(new Uri(imgPartUri, UriKind.Relative),
                        "image/png", CompressionOption.Normal);

                    using (var fs = File.OpenRead(sb.ScreenshotPath))
                        fs.CopyTo(imgPart.GetStream());

                    try
                    {
                        using (var img = Image.FromFile(sb.ScreenshotPath))
                            _imageSizes[rId] = new Size(img.Width, img.Height);
                    }
                    catch { }

                    imageRels[sb.Id] = rId;
                }

                // Document part
                var docPart = CreatePart(pkg, "/word/document.xml",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");

                // Document relationships (images)
                var docRels = pkg.CreatePart(new Uri("/word/_rels/document.xml.rels", UriKind.Relative),
                    "application/vnd.openxmlformats-officedocument.relationships+xml");

                var relXml = new StringBuilder();
                relXml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                relXml.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                relXml.Append("<Relationship Id=\"rId0\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
                foreach (var kv in imageRels)
                {
                    string imgName = $"media/img_{kv.Value.Replace("rId","")}.png";
                    relXml.Append($"<Relationship Id=\"{kv.Value}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{imgName}\"/>");
                }
                relXml.Append("</Relationships>");
                WriteXml(docRels, relXml.ToString());

                // Document body
                WriteXml(docPart, BuildDocumentXml(doc, imageRels));

                // Content types
                CreateContentTypes(pkg, imgIdx);
            }
        }

        // ─── DOCX XML builders ────────────────────────────────────────────────

        private static string BuildDocumentXml(ManualDocument doc, Dictionary<string, string> imageRels)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<w:document xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" ");
            sb.Append("xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" ");
            sb.Append("xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" ");
            sb.Append("xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\">");
            sb.Append("<w:body>");

            // 1. Cover Page
            sb.Append(BuildCoverPage(doc));

            // 2. Screen Index / Table of Contents
            sb.Append(BuildScreenIndex(doc));

            // 3. Document Sections
            foreach (var block in doc.Blocks.Where(b => b.IsIncluded).OrderBy(b => b.Order))
            {
                switch (block.Type)
                {
                    case ManualBlockType.Heading:
                        string headStyle = block.HeadingLevel > 0
                            ? $"Heading{Math.Min(3, block.HeadingLevel)}"
                            : (block.Order == 0 ? "Heading1" : "Heading2");

                        if (block.HeadingLevel <= 1 && !string.IsNullOrEmpty(block.Content))
                            sb.Append(Para($"📁 {block.Content}", "Breadcrumb", block.PageBreakBefore));

                        sb.Append(Para(block.Title, headStyle, block.PageBreakBefore && string.IsNullOrEmpty(block.Content)));
                        break;

                    case ManualBlockType.Description:
                        if (!string.IsNullOrEmpty(block.Title))
                            sb.Append(Para($"📝 {block.Title}", "SubHeading"));
                        foreach (var line in (block.Content ?? "").Replace("\r", "").Split('\n'))
                        {
                            string t = line.Trim();
                            if (t.Length == 0) continue;
                            bool isGroup = t.StartsWith("[") && t.EndsWith("]");
                            sb.Append(Para(isGroup ? t.Trim('[', ']') : t, isGroup ? "SubHeading" : "Normal"));
                        }
                        sb.Append(Para("", "Normal"));
                        break;

                    case ManualBlockType.Table:
                        if (!string.IsNullOrEmpty(block.Title))
                            sb.Append(Para($"🎛 {block.Title}", "SubHeading"));
                        if (block.TableRows != null && block.TableRows.Count > 0)
                            sb.Append(BuildTable(block.TableRows));
                        sb.Append(Para("", "Normal"));
                        break;

                    case ManualBlockType.Screenshot:
                        if (imageRels.TryGetValue(block.Id, out string rId))
                        {
                            sb.Append(BuildImagePara(rId));
                            sb.Append(ParaCaption(block.Title));

                            if (block.Callouts != null && block.Callouts.Count > 0)
                                sb.Append(BuildCalloutTable(block.Callouts));
                        }
                        sb.Append(Para("", "Normal"));
                        break;

                    case ManualBlockType.ControlItem:
                        string calloutPrefix = block.CalloutNumber.HasValue
                            ? $"{GetCircleNumber(block.CalloutNumber.Value)} "
                            : "";

                        sb.Append(Para($"{calloutPrefix}{block.Title}", "Heading3"));

                        if (!string.IsNullOrEmpty(block.Content))
                        {
                            foreach (var line in block.Content.Split('\n'))
                                sb.Append(Para(line.Trim(), "Normal"));
                        }

                        if (block.Steps != null && block.Steps.Count > 0)
                        {
                            sb.Append(Para("사용 방법:", "Normal"));
                            for (int i = 0; i < block.Steps.Count; i++)
                                sb.Append(ParaNumbered(block.Steps[i], i + 1));
                        }

                        if (block.SourceEvidence != null && block.SourceEvidence.Count > 0)
                        {
                            sb.Append(Para($"[소스 참조: {string.Join(", ", block.SourceEvidence)}]", "Caption"));
                        }
                        break;

                    case ManualBlockType.Warning:
                        sb.Append(BuildWarningBox(block.Title, block.Content));
                        break;

                    case ManualBlockType.Procedure:
                        sb.Append(BuildProcedureBox(block.Title, block.Content ?? string.Join("\n", block.Steps ?? new List<string>())));
                        break;
                }
            }

            // Section properties (A4 portrait: 11906 x 16838 dxa with 1" = 1440 dxa margins)
            sb.Append("<w:sectPr>");
            sb.Append("<w:pgSz w:w=\"11906\" w:h=\"16838\"/>");
            sb.Append("<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/>");
            sb.Append("</w:sectPr>");

            sb.Append("</w:body></w:document>");
            return sb.ToString();
        }

        // ─── Cover & Index Builders ───────────────────────────────────────────

        private static string BuildCoverPage(ManualDocument doc)
        {
            var sb = new StringBuilder();

            // Decorative top badge
            sb.Append("<w:p><w:pPr><w:spacing w:before=\"720\" w:after=\"180\"/><w:jc w:val=\"left\"/></w:pPr>" +
                      "<w:r><w:rPr><w:b/><w:sz w:val=\"20\"/><w:color w:val=\"2563EB\"/></w:rPr>" +
                      "<w:t>SYSTEM USER MANUAL  |  시스템 사용자 매뉴얼</w:t></w:r></w:p>");

            // Document Title
            sb.Append("<w:p><w:pPr><w:spacing w:before=\"100\" w:after=\"160\"/><w:jc w:val=\"left\"/></w:pPr>" +
                      $"<w:r><w:rPr><w:b/><w:sz w:val=\"56\"/><w:color w:val=\"0F2C59\"/></w:rPr>" +
                      $"<w:t>{XmlEscape(doc.Title)}</w:t></w:r></w:p>");

            // Subtitle
            string sub = !string.IsNullOrEmpty(doc.Subtitle) ? doc.Subtitle : doc.ApplicationName;
            if (!string.IsNullOrEmpty(sub))
            {
                sb.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"600\"/><w:jc w:val=\"left\"/></w:pPr>" +
                          $"<w:r><w:rPr><w:sz w:val=\"26\"/><w:color w:val=\"475569\"/></w:rPr>" +
                          $"<w:t>{XmlEscape(sub)}</w:t></w:r></w:p>");
            }

            // Divider line
            sb.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"2563EB\"/></w:pBdr>" +
                      "<w:spacing w:before=\"0\" w:after=\"720\"/></w:pPr><w:r><w:t></w:t></w:r></w:p>");

            // Metadata summary card
            int screenCount = doc.Blocks.Count(b => b.Type == ManualBlockType.Screenshot);
            var metaRows = new List<string[]>
            {
                new[] { "대상 애플리케이션", string.IsNullOrEmpty(doc.ApplicationName) ? "WPF Application" : doc.ApplicationName },
                new[] { "문서 작성 일시", string.IsNullOrEmpty(doc.CapturedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : doc.CapturedAt },
                new[] { "수록 화면 수", $"{Math.Max(1, screenCount)}개 UI 화면" },
                new[] { "매뉴얼 버전", "v1.0 (자동 생성)" }
            };

            sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"9000\" w:type=\"dxa\"/>" +
                      "<w:tblBorders>" +
                      "<w:top w:val=\"single\" w:sz=\"6\" w:color=\"CBD5E1\"/><w:left w:val=\"none\"/>" +
                      "<w:bottom w:val=\"single\" w:sz=\"6\" w:color=\"CBD5E1\"/><w:right w:val=\"none\"/>" +
                      "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"E2E8F0\"/><w:insideV w:val=\"none\"/>" +
                      "</w:tblBorders>" +
                      TableCellPadding + "</w:tblPr><w:tblGrid>" +
                      "<w:gridCol w:w=\"2600\"/><w:gridCol w:w=\"6400\"/>" +
                      "</w:tblGrid>");

            foreach (var r in metaRows)
            {
                sb.Append("<w:tr><w:trPr><w:cantSplit/></w:trPr>");
                sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"2600\" w:type=\"dxa\"/>" +
                          "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F1F5F9\"/></w:tcPr>" +
                          $"<w:p><w:pPr><w:spacing w:before=\"60\" w:after=\"60\"/></w:pPr>" +
                          $"<w:r><w:rPr><w:b/><w:sz w:val=\"20\"/><w:color w:val=\"334155\"/></w:rPr><w:t>{XmlEscape(r[0])}</w:t></w:r></w:p></w:tc>");
                sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"6400\" w:type=\"dxa\"/>" +
                          "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FFFFFF\"/></w:tcPr>" +
                          $"<w:p><w:pPr><w:spacing w:before=\"60\" w:after=\"60\"/></w:pPr>" +
                          $"<w:r><w:rPr><w:sz w:val=\"20\"/><w:color w:val=\"0F172A\"/></w:rPr><w:t>{XmlEscape(r[1])}</w:t></w:r></w:p></w:tc>");
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");

            // Page break
            sb.Append("<w:p><w:pPr><w:spacing w:before=\"480\"/><w:jc w:val=\"left\"/></w:pPr><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            return sb.ToString();
        }

        private static string BuildScreenIndex(ManualDocument doc)
        {
            var screens = doc.Blocks.Where(b => b.Type == ManualBlockType.Heading && b.HeadingLevel == 1).ToList();
            if (screens.Count < 2) return "";

            var sb = new StringBuilder();
            sb.Append(Para("화면 목록 및 구성 요약", "Heading1"));
            sb.Append(Para("본 매뉴얼에 수록된 UI 화면 및 메뉴 구성 목록입니다.", "Normal"));

            var rows = new List<string[]>();
            rows.Add(new[] { "순번", "화면 명칭", "메뉴 경로 / 뷰", "설명 포함 여부" });

            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                string path = !string.IsNullOrEmpty(s.Content) ? s.Content : s.Title;
                rows.Add(new[] { (i + 1).ToString(), s.Title, path, "수록" });
            }

            sb.Append(BuildTable(rows, new[] { 800, 3200, 3800, 1200 }));
            sb.Append("<w:p><w:pPr><w:spacing w:before=\"240\"/><w:jc w:val=\"left\"/></w:pPr><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            return sb.ToString();
        }

        // ─── Box & Paragraph Helpers ───────────────────────────────────────────

        private static string Para(string text, string style, bool pageBreakBefore = false)
        {
            string escaped = XmlEscape(text ?? "");
            string pb = pageBreakBefore ? "<w:pageBreakBefore/>" : "";
            return $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{pb}</w:pPr>" +
                   $"<w:r><w:t xml:space=\"preserve\">{escaped}</w:t></w:r></w:p>";
        }

        private static string ParaCaption(string text)
        {
            string escaped = XmlEscape(text ?? "");
            return $"<w:p><w:pPr><w:pStyle w:val=\"Caption\"/><w:keepNext/></w:pPr>" +
                   $"<w:r><w:t xml:space=\"preserve\">{escaped}</w:t></w:r></w:p>";
        }

        private static string ParaNumbered(string text, int num)
        {
            string escaped = XmlEscape(text ?? "");
            return $"<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr>" +
                   $"<w:r><w:t xml:space=\"preserve\">{num}. {escaped}</w:t></w:r></w:p>";
        }

        private static string BuildWarningBox(string title, string content)
        {
            var sb = new StringBuilder();
            sb.Append("<w:p><w:pPr><w:pStyle w:val=\"Warning\"/><w:keepNext/></w:pPr>" +
                      $"<w:r><w:t>⚠️ {XmlEscape(title ?? "주의 및 확인사항")}</w:t></w:r></w:p>");
            if (!string.IsNullOrEmpty(content))
            {
                foreach (var line in content.Replace("\r", "").Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    sb.Append("<w:p><w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"24\" w:color=\"F59E0B\"/></w:pBdr>" +
                              "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FFFBEB\"/>" +
                              "<w:ind w:left=\"240\" w:right=\"120\"/><w:spacing w:before=\"40\" w:after=\"60\"/></w:pPr>" +
                              $"<w:r><w:rPr><w:color w:val=\"92400E\"/></w:rPr><w:t xml:space=\"preserve\">{XmlEscape(t)}</w:t></w:r></w:p>");
                }
            }
            sb.Append(Para("", "Normal"));
            return sb.ToString();
        }

        private static string BuildProcedureBox(string title, string content)
        {
            var sb = new StringBuilder();
            sb.Append(Para($"📋 {title ?? "주요 작업 절차"}", "SubHeading"));
            if (!string.IsNullOrEmpty(content))
            {
                var lines = content.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    bool isStepHeader = line.StartsWith("[") && line.EndsWith("]");
                    if (isStepHeader)
                    {
                        sb.Append("<w:p><w:pPr><w:spacing w:before=\"140\" w:after=\"40\"/><w:keepNext/></w:pPr>" +
                                  $"<w:r><w:rPr><w:b/><w:color w:val=\"1E40AF\"/></w:rPr><w:t xml:space=\"preserve\">{XmlEscape(line.Trim('[', ']'))}</w:t></w:r></w:p>");
                    }
                    else
                    {
                        sb.Append("<w:p><w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"16\" w:color=\"3B82F6\"/></w:pBdr>" +
                                  "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F8FAFC\"/>" +
                                  "<w:ind w:left=\"240\" w:right=\"120\"/><w:spacing w:before=\"30\" w:after=\"50\"/></w:pPr>" +
                                  $"<w:r><w:rPr><w:color w:val=\"1E293B\"/></w:rPr><w:t xml:space=\"preserve\">{XmlEscape(line)}</w:t></w:r></w:p>");
                    }
                }
            }
            sb.Append(Para("", "Normal"));
            return sb.ToString();
        }

        // ─── Image Generation with LineRule="Auto" ────────────────────────────

        private static readonly Dictionary<string, Size> _imageSizes = new Dictionary<string, Size>();
        private static int _drawingId;

        private static string BuildImagePara(string rId)
        {
            // A4 page: width 11906 dxa, margin 1440 dxa on each side → printable width = 9026 dxa = 5,731,530 EMU (15.8 cm).
            // Page height 16838 dxa, margin 1440 dxa top/bottom → printable height = 13958 dxa = 8,863,330 EMU (24.6 cm).
            const long MAX_W = 5200000L;
            const long MAX_H = 3800000L;
            long EMU_W = MAX_W, EMU_H = 2925000L;
            if (_imageSizes.TryGetValue(rId, out Size px) && px.Width > 0 && px.Height > 0)
            {
                double ratio = (double)px.Height / px.Width;
                EMU_W = MAX_W;
                EMU_H = (long)(MAX_W * ratio);
                if (EMU_H > MAX_H)
                {
                    EMU_H = MAX_H;
                    EMU_W = (long)(MAX_H / ratio);
                }
            }
            int docPrId = ++_drawingId;
            return
                "<w:p><w:pPr>" +
                "<w:pStyle w:val=\"ManualScreenshot\"/>" +
                "<w:jc w:val=\"center\"/>" +
                "<w:spacing w:before=\"140\" w:after=\"80\"/>" +
                "<w:keepNext/>" +
                "</w:pPr><w:r><w:drawing>" +
                "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                $"<wp:extent cx=\"{EMU_W}\" cy=\"{EMU_H}\"/>" +
                $"<wp:docPr id=\"{docPrId}\" name=\"Screenshot {docPrId}\"/>" +
                "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                $"<pic:nvPicPr><pic:cNvPr id=\"{docPrId}\" name=\"img{docPrId}.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                "<pic:blipFill>" +
                $"<a:blip r:embed=\"{rId}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"/>" +
                "<a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{EMU_W}\" cy=\"{EMU_H}\"/></a:xfrm>" +
                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
                "<a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"CBD5E1\"/></a:solidFill></a:ln>" +
                "</pic:spPr></pic:pic></a:graphicData></a:graphic>" +
                "</wp:inline></w:drawing></w:r></w:p>";
        }

        // ─── Table Builders ───────────────────────────────────────────────────

        private const string TableBorders =
            "<w:tblBorders>" +
            "<w:top w:val=\"single\" w:sz=\"6\" w:color=\"CBD5E1\"/><w:left w:val=\"none\"/>" +
            "<w:bottom w:val=\"single\" w:sz=\"8\" w:color=\"94A3B8\"/><w:right w:val=\"none\"/>" +
            "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"E2E8F0\"/><w:insideV w:val=\"none\"/>" +
            "</w:tblBorders>";

        private const string TableCellPadding =
            "<w:tblCellMar>" +
            "<w:top w:w=\"120\" w:type=\"dxa\"/>" +
            "<w:bottom w:w=\"120\" w:type=\"dxa\"/>" +
            "<w:left w:w=\"160\" w:type=\"dxa\"/>" +
            "<w:right w:w=\"160\" w:type=\"dxa\"/>" +
            "</w:tblCellMar>";

        private static string BuildTable(List<string[]> rows, int[] customWidths = null)
        {
            if (rows == null || rows.Count == 0) return "";
            int cols = rows.Max(r => r.Length);
            int[] widths = customWidths;
            if (widths == null || widths.Length != cols)
            {
                if (cols == 3) widths = new[] { 850, 2650, 5500 };
                else widths = Enumerable.Repeat(9000 / Math.Max(1, cols), cols).ToArray();
            }

            var sb = new StringBuilder();
            sb.Append($"<w:tbl><w:tblPr><w:tblW w:w=\"9000\" w:type=\"dxa\"/>{TableBorders}{TableCellPadding}</w:tblPr><w:tblGrid>");
            foreach (var w in widths) sb.Append($"<w:gridCol w:w=\"{w}\"/>");
            sb.Append("</w:tblGrid>");

            for (int r = 0; r < rows.Count; r++)
            {
                bool header = r == 0;
                sb.Append("<w:tr><w:trPr>");
                if (header) sb.Append("<w:tblHeader/>");
                sb.Append("<w:cantSplit/></w:trPr>");

                string rowShade = header ? "1E3A8A" : (r % 2 == 1 ? "F8FAFC" : "FFFFFF");

                for (int c = 0; c < cols; c++)
                {
                    string text = c < rows[r].Length ? rows[r][c] ?? "" : "";
                    sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{widths[c]}\" w:type=\"dxa\"/>" +
                              $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{rowShade}\"/></w:tcPr>");

                    var lines = text.Replace("\r", "").Split('\n');
                    for (int li = 0; li < lines.Length; li++)
                    {
                        string line = lines[li];
                        string jc = (c == 0 && !header) ? "<w:jc w:val=\"center\"/>" : "";
                        string rPr;
                        if (header)
                        {
                            rPr = "<w:rPr><w:b/><w:sz w:val=\"20\"/><w:color w:val=\"FFFFFF\"/></w:rPr>";
                        }
                        else if (c == 0)
                        {
                            rPr = "<w:rPr><w:b/><w:sz w:val=\"22\"/><w:color w:val=\"1E40AF\"/></w:rPr>";
                        }
                        else if (c == 1)
                        {
                            rPr = "<w:rPr><w:b/><w:sz w:val=\"20\"/><w:color w:val=\"1E293B\"/></w:rPr>";
                        }
                        else
                        {
                            rPr = "<w:rPr><w:sz w:val=\"20\"/><w:color w:val=\"334155\"/></w:rPr>";
                        }

                        int spaceAfter = (li == lines.Length - 1) ? 40 : 20;
                        int spaceBefore = (li == 0) ? 40 : 0;
                        sb.Append($"<w:p><w:pPr><w:spacing w:before=\"{spaceBefore}\" w:after=\"{spaceAfter}\"/>{jc}</w:pPr>" +
                                  $"<w:r>{rPr}<w:t xml:space=\"preserve\">{XmlEscape(line)}</w:t></w:r></w:p>");
                    }
                    sb.Append("</w:tc>");
                }
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            return sb.ToString();
        }

        private static string BuildCalloutTable(List<CalloutItem> callouts)
        {
            if (callouts == null || callouts.Count == 0) return "";
            var rows = new List<string[]>();
            rows.Add(new[] { "번호", "컨트롤 명칭" });
            foreach (var c in callouts)
            {
                rows.Add(new[] { GetCircleNumber(c.Number), c.Label ?? "" });
            }
            return BuildTable(rows, new[] { 1000, 8000 });
        }

        private static string GetCircleNumber(int n)
        {
            // Unicode circled digits ①–⑳
            if (n >= 1 && n <= 20)
                return ((char)(0x2460 + n - 1)).ToString();
            return $"({n})";
        }

        private static string XmlEscape(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");

        // ─── Styles XML ────────────────────────────────────────────────────────

        private static string BuildStylesXml() =>
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:docDefaults>
    <w:rPrDefault>
      <w:rPr>
        <w:rFonts w:ascii=""Segoe UI"" w:hAnsi=""Segoe UI"" w:eastAsia=""맑은 고딕"" w:cs=""Segoe UI""/>
        <w:sz w:val=""21""/>
        <w:szCs w:val=""21""/>
        <w:color w:val=""1E293B""/>
        <w:lang w:val=""ko-KR"" w:eastAsia=""ko-KR""/>
      </w:rPr>
    </w:rPrDefault>
    <w:pPrDefault>
      <w:pPr>
        <w:spacing w:before=""0"" w:after=""120"" w:line=""260"" w:lineRule=""auto""/>
      </w:pPr>
    </w:pPrDefault>
  </w:docDefaults>

  <w:style w:type=""paragraph"" w:styleId=""Normal"">
    <w:name w:val=""Normal""/>
    <w:pPr><w:spacing w:before=""0"" w:after=""100"" w:line=""240"" w:lineRule=""auto""/></w:pPr>
    <w:rPr><w:sz w:val=""21""/><w:color w:val=""1E293B""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""ManualScreenshot"">
    <w:name w:val=""Manual Screenshot""/>
    <w:pPr>
      <w:jc w:val=""center""/>
      <w:spacing w:before=""140"" w:after=""80""/>
    </w:pPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Heading1"">
    <w:name w:val=""heading 1""/>
    <w:pPr>
      <w:spacing w:before=""360"" w:after=""140""/>
      <w:pBdr><w:left w:val=""single"" w:sz=""24"" w:space=""10"" w:color=""2563EB""/></w:pBdr>
    </w:pPr>
    <w:rPr><w:b/><w:sz w:val=""36""/><w:color w:val=""0F2C59""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Heading2"">
    <w:name w:val=""heading 2""/>
    <w:pPr><w:spacing w:before=""240"" w:after=""100""/></w:pPr>
    <w:rPr><w:b/><w:sz w:val=""28""/><w:color w:val=""1E40AF""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Heading3"">
    <w:name w:val=""heading 3""/>
    <w:pPr><w:spacing w:before=""180"" w:after=""80""/></w:pPr>
    <w:rPr><w:b/><w:sz w:val=""24""/><w:color w:val=""334155""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""SubHeading"">
    <w:name w:val=""SubHeading""/>
    <w:pPr><w:spacing w:before=""200"" w:after=""60""/></w:pPr>
    <w:rPr><w:b/><w:sz w:val=""23""/><w:color w:val=""1E293B""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Caption"">
    <w:name w:val=""caption""/>
    <w:pPr><w:jc w:val=""center""/><w:spacing w:before=""60"" w:after=""160""/></w:pPr>
    <w:rPr><w:i/><w:sz w:val=""18""/><w:color w:val=""64748B""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Breadcrumb"">
    <w:name w:val=""Breadcrumb""/>
    <w:pPr><w:spacing w:before=""160"" w:after=""40""/></w:pPr>
    <w:rPr><w:b/><w:sz w:val=""18""/><w:color w:val=""2563EB""/></w:rPr>
  </w:style>

  <w:style w:type=""paragraph"" w:styleId=""Warning"">
    <w:name w:val=""Warning""/>
    <w:pPr>
      <w:pBdr><w:left w:val=""single"" w:sz=""24"" w:space=""8"" w:color=""F59E0B""/></w:pBdr>
      <w:shd w:val=""clear"" w:color=""auto"" w:fill=""FFFBEB""/>
      <w:ind w:left=""240"" w:right=""120""/>
      <w:spacing w:before=""140"" w:after=""60""/>
    </w:pPr>
    <w:rPr><w:b/><w:sz w:val=""21""/><w:color w:val=""B45309""/></w:rPr>
  </w:style>
</w:styles>";

        // ─── Package helpers ───────────────────────────────────────────────────

        private static void CreateRels(Package pkg)
        {
            var relsPart = pkg.CreatePart(new Uri("/_rels/.rels", UriKind.Relative),
                "application/vnd.openxmlformats-officedocument.relationship+xml");
            WriteXml(relsPart, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
        }

        private static void CreateContentTypes(Package pkg, int imageCount)
        {
            var ct = pkg.CreatePart(new Uri("/[Content_Types].xml", UriKind.Relative),
                "application/xml");
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-officedocument.relationship+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            sb.Append("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>");
            sb.Append("<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>");
            sb.Append("</Types>");
            WriteXml(ct, sb.ToString());
        }

        private static PackagePart CreatePart(Package pkg, string uri, string contentType)
            => pkg.CreatePart(new Uri(uri, UriKind.Relative), contentType, CompressionOption.Normal);

        private static void WriteXml(PackagePart part, string xml)
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            using (var stream = part.GetStream())
                stream.Write(bytes, 0, bytes.Length);
        }
    }
}
