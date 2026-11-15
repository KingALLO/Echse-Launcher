using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Echse.Core;

internal static class StoreHtml
{
    public static string Text(string html, bool preserveLines = false)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder();
        void Append(INode node)
        {
            if (node is IElement element && element.LocalName is "script" or "style") return;
            var block = preserveLines && node is IElement tag &&
                tag.LocalName is "br" or "li" or "p" or "div" or "ul" or "h1" or "h2" or "h3";
            if (block) text.AppendLine();
            if (node is IText content) text.Append(content.Data);
            foreach (var child in node.ChildNodes) Append(child);
            if (block) text.AppendLine();
        }
        Append(document.Body!);
        return preserveLines
            ? string.Join("\n", text.ToString().Split('\n').Select(Normalize).Where(line => line.Length > 0))
            : Normalize(text.ToString());
    }

    private static string Normalize(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Trim();
}
