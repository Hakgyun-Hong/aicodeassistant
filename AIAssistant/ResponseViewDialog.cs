using System;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AIAssistant
{
    /// <summary>
    /// Renders LLM response as HTML in a dark-themed window.
    /// Shows timestamp and prompt in a collapsible header, with Copy HTML button.
    /// </summary>
    public class ResponseViewDialog : Form
    {
        private WebBrowser webBrowser;
        private string htmlContent;

        public ResponseViewDialog(string actionName, string prompt, string response)
        {
            htmlContent = BuildHtml(actionName, prompt, response);
            InitializeUI(actionName);
        }

        private void InitializeUI(string actionName)
        {
            this.Text = $"AI Assistant — {actionName}";
            this.Width = 900;
            this.Height = 700;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.MinimumSize = new Size(600, 400);
            this.Icon = SystemIcons.Information;

            // Top toolbar
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = Color.FromArgb(37, 37, 38),
                Padding = new Padding(8, 6, 8, 6)
            };

            var btnCopyHtml = new Button
            {
                Text = "📋 Copy HTML",
                Height = 26,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(14, 99, 156),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                Dock = DockStyle.Left
            };
            btnCopyHtml.FlatAppearance.BorderSize = 0;
            btnCopyHtml.Click += (s, e) =>
            {
                Clipboard.SetText(htmlContent);
                btnCopyHtml.Text = "✅ Copied!";
                var timer = new Timer { Interval = 2000 };
                timer.Tick += (ts, te) => { btnCopyHtml.Text = "📋 Copy HTML"; timer.Stop(); timer.Dispose(); };
                timer.Start();
            };

            var btnCopyText = new Button
            {
                Text = "📄 Copy Text",
                Height = 26,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(212, 212, 212),
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                Dock = DockStyle.Left,
                Margin = new Padding(5, 0, 0, 0)
            };
            btnCopyText.FlatAppearance.BorderSize = 0;
            btnCopyText.Click += (s, e) =>
            {
                string plainText = webBrowser.Document?.Body?.InnerText ?? "";
                Clipboard.SetText(plainText);
                btnCopyText.Text = "✅ Copied!";
                var timer = new Timer { Interval = 2000 };
                timer.Tick += (ts, te) => { btnCopyText.Text = "📄 Copy Text"; timer.Stop(); timer.Dispose(); };
                timer.Start();
            };

            var btnClose = new Button
            {
                Text = "Close",
                Height = 26,
                Width = 60,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(212, 212, 212),
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                Dock = DockStyle.Right
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();

            toolbar.Controls.Add(btnCopyText);
            toolbar.Controls.Add(btnCopyHtml);
            toolbar.Controls.Add(btnClose);

            // WebBrowser for HTML rendering
            webBrowser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                IsWebBrowserContextMenuEnabled = false,
                AllowNavigation = false,
                ScriptErrorsSuppressed = true
            };
            webBrowser.DocumentCompleted += (s, e) =>
            {
                // Prevent link navigation
                if (webBrowser.Document != null)
                {
                    webBrowser.Document.Body.Style = "overflow-y: auto;";
                }
            };

            this.Controls.Add(webBrowser);
            this.Controls.Add(toolbar);

            // Load HTML after form is shown
            this.Shown += (s, e) =>
            {
                webBrowser.DocumentText = htmlContent;
            };

            // ESC to close
            this.KeyPreview = true;
            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) this.Close();
            };
        }

        /// <summary>
        /// Converts LLM markdown-like response to styled HTML.
        /// </summary>
        private static string BuildHtml(string actionName, string prompt, string response)
        {
            string responseHtml = MarkdownToHtml(response);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string escapedPrompt = System.Net.WebUtility.HtmlEncode(prompt);

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{
    background: #1e1e1e;
    color: #d4d4d4;
    font-family: 'Segoe UI', -apple-system, sans-serif;
    font-size: 14px;
    line-height: 1.6;
    padding: 24px 32px;
  }}

  /* Header section - subtle, collapsible */
  .meta {{
    background: #252526;
    border: 1px solid #3c3c3c;
    border-radius: 6px;
    padding: 12px 16px;
    margin-bottom: 20px;
    font-size: 12px;
    color: #9d9da0;
  }}
  .meta-title {{
    color: #4fc1ff;
    font-weight: 600;
    font-size: 13px;
    margin-bottom: 4px;
  }}
  .meta-time {{
    color: #6a9955;
    font-size: 11px;
  }}
  .meta-prompt {{
    color: #9d9da0;
    font-size: 12px;
    margin-top: 6px;
    padding-top: 6px;
    border-top: 1px solid #3c3c3c;
    white-space: pre-wrap;
    max-height: 80px;
    overflow-y: auto;
  }}

  /* Main response area */
  .response {{
    padding: 4px 0;
  }}

  h1, h2, h3, h4 {{
    color: #4fc1ff;
    margin: 18px 0 8px 0;
    font-weight: 600;
  }}
  h1 {{ font-size: 22px; border-bottom: 1px solid #3c3c3c; padding-bottom: 6px; }}
  h2 {{ font-size: 18px; }}
  h3 {{ font-size: 15px; color: #ce9178; }}

  p {{ margin: 8px 0; }}

  ul, ol {{
    margin: 8px 0 8px 24px;
  }}
  li {{
    margin: 4px 0;
  }}

  strong, b {{ color: #dcdcaa; }}
  em, i {{ color: #c586c0; }}

  code {{
    background: #2d2d30;
    color: #ce9178;
    padding: 2px 6px;
    border-radius: 3px;
    font-family: 'Consolas', 'Courier New', monospace;
    font-size: 13px;
  }}

  pre {{
    background: #1a1a1a;
    border: 1px solid #3c3c3c;
    border-radius: 6px;
    padding: 14px 18px;
    margin: 12px 0;
    overflow-x: auto;
    position: relative;
  }}
  pre code {{
    background: transparent;
    padding: 0;
    color: #d4d4d4;
    font-size: 13px;
    line-height: 1.5;
  }}
  pre .lang-tag {{
    position: absolute;
    top: 6px;
    right: 10px;
    font-size: 10px;
    color: #6a6a6a;
    font-family: 'Segoe UI', sans-serif;
    text-transform: uppercase;
  }}

  /* Keyword highlighting hints */
  .kw {{ color: #569cd6; }}
  .str {{ color: #ce9178; }}
  .cmt {{ color: #6a9955; }}
  .num {{ color: #b5cea8; }}

  blockquote {{
    border-left: 3px solid #4fc1ff;
    padding: 8px 16px;
    margin: 12px 0;
    background: #252526;
    border-radius: 0 4px 4px 0;
    color: #c0c0c0;
  }}

  hr {{
    border: none;
    border-top: 1px solid #3c3c3c;
    margin: 16px 0;
  }}

  table {{
    border-collapse: collapse;
    margin: 12px 0;
    width: 100%;
  }}
  th, td {{
    border: 1px solid #3c3c3c;
    padding: 8px 12px;
    text-align: left;
  }}
  th {{
    background: #2d2d30;
    color: #4fc1ff;
    font-weight: 600;
  }}

  /* Scrollbar */
  ::-webkit-scrollbar {{ width: 8px; }}
  ::-webkit-scrollbar-track {{ background: #1e1e1e; }}
  ::-webkit-scrollbar-thumb {{ background: #3c3c3c; border-radius: 4px; }}
  ::-webkit-scrollbar-thumb:hover {{ background: #505050; }}
</style>
</head>
<body>
  <div class=""meta"">
    <div class=""meta-title"">{System.Net.WebUtility.HtmlEncode(actionName)}</div>
    <div class=""meta-time"">🕐 {timestamp}</div>
    <div class=""meta-prompt"">💬 {escapedPrompt}</div>
  </div>
  <div class=""response"">
    {responseHtml}
  </div>
</body>
</html>";
        }

        /// <summary>
        /// Basic Markdown to HTML converter.
        /// </summary>
        private static string MarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            var sb = new StringBuilder();
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            bool inList = false;
            string codeBlockLang = "";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Code block toggle
                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCodeBlock)
                    {
                        if (inList) { sb.Append("</ul>"); inList = false; }
                        codeBlockLang = line.TrimStart().Substring(3).Trim();
                        string langTag = string.IsNullOrEmpty(codeBlockLang) ? "" :
                            $"<span class=\"lang-tag\">{Esc(codeBlockLang)}</span>";
                        sb.Append($"<pre>{langTag}<code>");
                        inCodeBlock = true;
                    }
                    else
                    {
                        sb.Append("</code></pre>");
                        inCodeBlock = false;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    sb.AppendLine(Esc(line));
                    continue;
                }

                // Empty line
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    continue;
                }

                // Headers
                if (line.StartsWith("####"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append($"<h4>{InlineFormat(line.Substring(4).Trim())}</h4>");
                    continue;
                }
                if (line.StartsWith("###"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append($"<h3>{InlineFormat(line.Substring(3).Trim())}</h3>");
                    continue;
                }
                if (line.StartsWith("##"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append($"<h2>{InlineFormat(line.Substring(2).Trim())}</h2>");
                    continue;
                }
                if (line.StartsWith("#"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append($"<h1>{InlineFormat(line.Substring(1).Trim())}</h1>");
                    continue;
                }

                // Horizontal rule
                if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append("<hr/>");
                    continue;
                }

                // Numbered list
                var numMatch = Regex.Match(line, @"^\s*(\d+)\.\s+(.+)$");
                if (numMatch.Success)
                {
                    if (!inList) { sb.Append("<ul>"); inList = true; }
                    sb.Append($"<li><strong>{numMatch.Groups[1].Value}.</strong> {InlineFormat(numMatch.Groups[2].Value)}</li>");
                    continue;
                }

                // Bullet list
                var bulletMatch = Regex.Match(line, @"^\s*[-*+]\s+(.+)$");
                if (bulletMatch.Success)
                {
                    if (!inList) { sb.Append("<ul>"); inList = true; }
                    sb.Append($"<li>{InlineFormat(bulletMatch.Groups[1].Value)}</li>");
                    continue;
                }

                // Blockquote
                if (line.TrimStart().StartsWith(">"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append($"<blockquote>{InlineFormat(line.TrimStart().Substring(1).Trim())}</blockquote>");
                    continue;
                }

                // Normal paragraph
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append($"<p>{InlineFormat(line)}</p>");
            }

            if (inCodeBlock) sb.Append("</code></pre>");
            if (inList) sb.Append("</ul>");

            return sb.ToString();
        }

        /// <summary>
        /// Inline markdown formatting: bold, italic, inline code.
        /// </summary>
        private static string InlineFormat(string text)
        {
            text = Esc(text);
            // Bold **text** or __text__
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            text = Regex.Replace(text, @"__(.+?)__", "<strong>$1</strong>");
            // Italic *text* or _text_
            text = Regex.Replace(text, @"\*(.+?)\*", "<em>$1</em>");
            text = Regex.Replace(text, @"(?<!\w)_(.+?)_(?!\w)", "<em>$1</em>");
            // Inline code `text`
            text = Regex.Replace(text, @"`(.+?)`", "<code>$1</code>");
            return text;
        }

        private static string Esc(string s)
        {
            return System.Net.WebUtility.HtmlEncode(s);
        }
    }
}
