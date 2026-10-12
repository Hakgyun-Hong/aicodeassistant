using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace AIAssistant
{
    /// <summary>
    /// Parses the application's UI definition XML into a ManualPage tree.
    ///
    ///   &lt;UI&gt;
    ///     &lt;MenuList&gt;
    ///       &lt;Menu&gt;
    ///         &lt;DisplayName&gt;CTC&lt;/DisplayName&gt;
    ///         &lt;ViewName /&gt;                       ← group node (no view)
    ///         &lt;IsHome&gt;false&lt;/IsHome&gt;
    ///         &lt;MenuList&gt;                          ← recurse unless empty (&lt;MenuList/&gt;)
    ///           &lt;Menu&gt;&lt;DisplayName&gt;Main&lt;/DisplayName&gt;&lt;ViewName&gt;MainLayout_View&lt;/ViewName&gt;...
    ///
    /// Element names are matched by LocalName so namespaces don't matter.
    /// </summary>
    public static class UiMenuXmlParser
    {
        public static List<ManualPage> Parse(string xmlPath)
        {
            var doc = LoadTolerant(xmlPath);
            var root = doc.DocumentElement;
            if (root == null) return new List<ManualPage>();

            // Top-level <MenuList> directly under root; if absent, search anywhere (first occurrence).
            var menuList = Child(root, "MenuList")
                        ?? doc.GetElementsByTagName("MenuList").Cast<XmlElement>().FirstOrDefault();
            if (menuList == null)
                throw new InvalidDataException("XML에서 <MenuList> 요소를 찾을 수 없습니다.");

            return ParseMenuList(menuList, new List<string>(), 0);
        }

        private static List<ManualPage> ParseMenuList(XmlElement menuList, List<string> parentPath, int depth)
        {
            var pages = new List<ManualPage>();
            if (depth > 16) return pages; // guard against pathological nesting

            foreach (var menu in Children(menuList, "Menu"))
            {
                string display = Text(menu, "DisplayName");
                string view    = Text(menu, "ViewName");
                bool   isHome  = string.Equals(Text(menu, "IsHome"), "true", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(view))
                    continue;

                var path = new List<string>(parentPath) { string.IsNullOrWhiteSpace(display) ? view : display };
                var page = new ManualPage
                {
                    DisplayName = string.IsNullOrWhiteSpace(display) ? view : display,
                    ViewName    = view ?? "",
                    IsHome      = isHome,
                    Depth       = depth,
                    NavPath     = path
                };

                // Recurse only if the nested <MenuList> actually contains <Menu> children.
                var sub = Child(menu, "MenuList");
                if (sub != null && Children(sub, "Menu").Any())
                    page.Children = ParseMenuList(sub, path, depth + 1);

                pages.Add(page);
            }
            return pages;
        }

        // ─── XML helpers (namespace-agnostic) ───────────────────────────────

        private static XmlDocument LoadTolerant(string path)
        {
            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                doc.Load(path);
                return doc;
            }
            catch (XmlException)
            {
                // Files edited in word processors sometimes contain smart quotes in attributes.
                string text = File.ReadAllText(path, Encoding.UTF8)
                    .Replace('\u201C', '"').Replace('\u201D', '"')
                    .Replace('\u2018', '\'').Replace('\u2019', '\'');
                doc.LoadXml(text);
                return doc;
            }
        }

        private static XmlElement Child(XmlElement parent, string localName) =>
            Children(parent, localName).FirstOrDefault();

        private static IEnumerable<XmlElement> Children(XmlElement parent, string localName) =>
            parent.ChildNodes.OfType<XmlElement>()
                  .Where(e => string.Equals(e.LocalName, localName, StringComparison.OrdinalIgnoreCase));

        private static string Text(XmlElement parent, string localName) =>
            Child(parent, localName)?.InnerText?.Trim() ?? "";
    }
}
