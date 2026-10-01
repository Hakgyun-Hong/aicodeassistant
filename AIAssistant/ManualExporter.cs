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
    /// Exports a ManualDocument to DOCX (Office Open XML).
    ///
    /// Uses System.IO.Packaging (in WindowsBase.dll — already referenced)
    /// + raw Open XML generation.  No external NuGet required.
    ///
    /// DOCX structure produced:
    ///   [Content_Types].xml
    ///   _rels/.rels
    ///   word/document.xml   ← main content
    ///   word/styles.xml     ← heading / body / callout styles
    ///   word/_rels/document.xml.rels
    ///   word/media/img_XXX.png  ← one per screenshot block
    ///
    /// Styles: Heading1, Heading2, Normal, Caption, Warning
    /// </summary>
    public static class ManualExporter
    {
        // ─── Public API ──────────────────────────────────────────────────────

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

            // Document title
            sb.Append(Para(doc.Title, "Heading1"));
            sb.Append(Para($"작성일: {doc.CapturedAt}  |  캡처 모드: {(doc.Mode == CaptureMode.FullApp ? "전체 앱" : "현재 화면")}  |  대상: {doc.ApplicationName}", "Caption"));
            sb.Append(Para("", "Normal")); // spacer

            foreach (var block in doc.Blocks.Where(b => b.IsIncluded).OrderBy(b => b.Order))
            {
                switch (block.Type)
                {
                    case ManualBlockType.Heading:
                        string headStyle = block.Order == 0 ? "Heading1" : "Heading2";
                        sb.Append(Para(block.Title, headStyle));
                        if (!string.IsNullOrEmpty(block.Content))
                            sb.Append(Para(block.Content, "Normal"));
                        break;

                    case ManualBlockType.Screenshot:
                        if (imageRels.TryGetValue(block.Id, out string rId))
                        {
                            sb.Append(Para(block.Title, "Caption"));
                            sb.Append(BuildImagePara(rId));

                            // Callout legend table
                            if (block.Callouts != null && block.Callouts.Count > 0)
                                sb.Append(BuildCalloutTable(block.Callouts));
                        }
                        sb.Append(Para("", "Normal"));
                        break;

                    case ManualBlockType.ControlItem:
                        string calloutPrefix = block.CalloutNumber.HasValue
                            ? $"({GetCircleNumber(block.CalloutNumber.Value)}) "
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
                        sb.Append(Para($"⚠️ {block.Title}", "Warning"));
                        if (!string.IsNullOrEmpty(block.Content))
                            sb.Append(Para(block.Content, "Normal"));
                        break;

                    case ManualBlockType.Procedure:
                        sb.Append(Para(block.Title, "Heading3"));
                        for (int i = 0; i < block.Steps.Count; i++)
                            sb.Append(ParaNumbered(block.Steps[i], i + 1));
                        break;
                }
            }

            // Section properties (A4 portrait)
            sb.Append("<w:sectPr>");
            sb.Append("<w:pgSz w:w=\"11906\" w:h=\"16838\"/>");
            sb.Append("<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/>");
            sb.Append("</w:sectPr>");

            sb.Append("</w:body></w:document>");
            return sb.ToString();
        }

        // ─── XML paragraph helpers ─────────────────────────────────────────────

        private static string Para(string text, string style)
        {
            string escaped = XmlEscape(text ?? "");
            return $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>" +
                   $"<w:r><w:t xml:space=\"preserve\">{escaped}</w:t></w:r></w:p>";
        }

        private static string ParaNumbered(string text, int num)
        {
            string escaped = XmlEscape(text ?? "");
            return $"<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr>" +
                   $"<w:r><w:t xml:space=\"preserve\">{num}. {escaped}</w:t></w:r></w:p>";
        }

        private static string BuildImagePara(string rId)
        {
            // 14400000 EMUs = ~15cm width (A4 content width ~12400000 → use 10800000 ≈ 11.25cm)
            const long EMU_W = 10800000L;
            const long EMU_H = 7200000L;
            return
                "<w:p><w:r><w:rPr/><w:drawing>" +
                "<wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                $"<wp:extent cx=\"{EMU_W}\" cy=\"{EMU_H}\"/>" +
                "<wp:docPr id=\"1\" name=\"Screenshot\"/>" +
                "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                "<pic:blipFill>" +
                $"<a:blip r:embed=\"{rId}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"/>" +
                "<a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                $"<pic:spPr><a:xfrm><a:ext cx=\"{EMU_W}\" cy=\"{EMU_H}\"/></a:xfrm>" +
                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
                "</pic:spPr></pic:pic></a:graphicData></a:graphic>" +
                "</wp:inline></w:drawing></w:r></w:p>";
        }

        private static string BuildCalloutTable(List<CalloutItem> callouts)
        {
            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr>");
            // Header row
            sb.Append("<w:tr>");
            sb.Append(TableCell("번호", true));
            sb.Append(TableCell("컨트롤", true));
            sb.Append("</w:tr>");
            foreach (var c in callouts)
            {
                sb.Append("<w:tr>");
                sb.Append(TableCell(GetCircleNumber(c.Number)));
                sb.Append(TableCell(c.Label));
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            return sb.ToString();
        }

        private static string TableCell(string text, bool bold = false)
        {
            string boldTag = bold ? "<w:rPr><w:b/></w:rPr>" : "";
            return $"<w:tc><w:p><w:r>{boldTag}<w:t>{XmlEscape(text)}</w:t></w:r></w:p></w:tc>";
        }

        private static string GetCircleNumber(int n)
        {
            // Unicode circled digits ①–㉚
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
<w:style w:type=""paragraph"" w:styleId=""Normal""><w:name w:val=""Normal""/>
  <w:pPr><w:spacing w:after=""120""/></w:pPr>
  <w:rPr><w:sz w:val=""22""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Heading1""><w:name w:val=""heading 1""/>
  <w:pPr><w:spacing w:before=""240"" w:after=""120""/></w:pPr>
  <w:rPr><w:b/><w:sz w:val=""36""/><w:color w:val=""1F3864""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Heading2""><w:name w:val=""heading 2""/>
  <w:pPr><w:spacing w:before=""200"" w:after=""100""/></w:pPr>
  <w:rPr><w:b/><w:sz w:val=""28""/><w:color w:val=""2E75B6""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Heading3""><w:name w:val=""heading 3""/>
  <w:pPr><w:spacing w:before=""160"" w:after=""80""/></w:pPr>
  <w:rPr><w:b/><w:sz w:val=""24""/><w:color w:val=""2E75B6""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Caption""><w:name w:val=""caption""/>
  <w:pPr><w:jc w:val=""center""/></w:pPr>
  <w:rPr><w:i/><w:sz w:val=""18""/><w:color w:val=""595959""/></w:rPr></w:style>
<w:style w:type=""paragraph"" w:styleId=""Warning""><w:name w:val=""Warning""/>
  <w:pPr><w:pBdr><w:left w:val=""single"" w:sz=""6"" w:color=""FF0000""/></w:pBdr><w:ind w:left=""360""/></w:pPr>
  <w:rPr><w:b/><w:color w:val=""C00000""/></w:rPr></w:style>
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
