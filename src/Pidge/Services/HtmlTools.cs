using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Pidge.Services;

public static partial class HtmlTools
{
    /// <summary>Builds a safe, self-contained page for the reading pane. Scripts never run; remote content is opt-in.</summary>
    public static string BuildReaderPage(MimeMessage message, bool allowRemote, bool darkReading)
    {
        string content;
        string styles = "";
        if (message.HtmlBody is { } html)
        {
            html = InlineCidImages(message, html);
            styles = string.Concat(StyleBlock().Matches(html).Select(m => m.Value));
            content = BodyInner(html);
        }
        else
        {
            content = "<div class=\"qm-text\">" + TextToHtml(message.TextBody ?? "") + "</div>";
        }

        var remote = allowRemote ? " https: http:" : "";
        var csp = $"default-src 'none'; img-src data: cid:{remote}; style-src 'unsafe-inline'{remote}; font-src data:{remote}; media-src data:";
        var dark = darkReading
            ? "html{filter:invert(.9) hue-rotate(180deg);} img,video,picture,[style*='background-image']{filter:invert(1) hue-rotate(180deg);}"
            : "";

        return $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <base target="_blank">
            <style>
              html{background:#ffffff;}
              body{margin:0;padding:22px 26px;font-family:'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.5;color:#1d2027;word-wrap:break-word;overflow-wrap:anywhere;}
              img{max-width:100%;height:auto;}
              table{max-width:100% !important;}
              pre{white-space:pre-wrap;}
              .qm-text{white-space:pre-wrap;}
              blockquote{border-left:3px solid #d0d4dc;margin:8px 0;padding-left:12px;color:#5a6070;}
              a{color:#5b4fd6;}
              ::-webkit-scrollbar{width:10px;height:10px;} ::-webkit-scrollbar-thumb{background:#c5c9d3;border-radius:5px;border:2px solid #fff;}
              {{dark}}
            </style>
            {{styles}}
            </head><body>{{content}}</body></html>
            """;
    }

    public static bool HasRemoteContent(MimeMessage message) =>
        message.HtmlBody is { } h && RemoteRef().IsMatch(h);

    /// <summary>Extracts something safe to embed inside a reply/forward quote.</summary>
    public static string QuotableBody(MimeMessage message)
    {
        if (message.HtmlBody is { } html)
        {
            html = InlineCidImages(message, html);
            var inner = BodyInner(html);
            inner = ScriptBlock().Replace(inner, "");
            inner = StyleBlock().Replace(inner, "");
            return inner;
        }
        return "<div style=\"white-space:pre-wrap\">" + TextToHtml(message.TextBody ?? "") + "</div>";
    }

    public static string HtmlToText(string html)
    {
        var s = BreakTags().Replace(html, "\n");
        s = BlockEnd().Replace(s, "\n");
        s = ScriptBlock().Replace(s, "");
        s = StyleBlock().Replace(s, "");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string TextToHtml(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        return Linkify().Replace(escaped, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
    }

    private static string BodyInner(string html)
    {
        var m = BodyRegex().Match(html);
        var inner = m.Success ? m.Groups[1].Value : html;
        return HeadRegex().Replace(inner, "");
    }

    private static string InlineCidImages(MimeMessage message, string html)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase)) return html;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content == null) continue;
            using var ms = new System.IO.MemoryStream();
            part.Content.DecodeTo(ms);
            var uri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
            html = Regex.Replace(html, "cid:" + Regex.Escape(part.ContentId), uri, RegexOptions.IgnoreCase);
        }
        return html;
    }

    public static string Encode(string s) => WebUtility.HtmlEncode(s);

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();
    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();
    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRegex();
    [GeneratedRegex(@"<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();
    [GeneratedRegex(@"(src|background)\s*=\s*[""']?\s*https?://|url\(\s*[""']?https?://", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteRef();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();
    [GeneratedRegex(@"</(p|div|li|tr|h[1-6]|blockquote)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Linkify();
}
