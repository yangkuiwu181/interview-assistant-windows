using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace InterviewAssistant;

public static class DocumentService
{
    public static string ExtractText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => File.ReadAllText(path),
            ".pdf" => ReadPdf(path),
            ".docx" => ReadDocx(path),
            _ => throw new NotSupportedException("仅支持 PDF、DOCX 和 TXT 文件。")
        };
    }

    private static string ReadPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
    }

    private static string ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document.Body;
        return body is null ? "" : string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText));
    }

    public static string RelevantContext(IEnumerable<SourceDocument> documents, string question, int maxChars = 9000)
    {
        var query = Tokens(question).ToHashSet();
        var chunks = documents.SelectMany(doc => Chunk(doc.Text).Select((text, index) => new
        {
            doc.Kind,
            doc.Name,
            Text = text,
            Index = index,
            Score = Tokens(text).Count(query.Contains) + (doc.Kind == DocumentKind.Resume ? 2 : 0)
        })).OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToList();
        var selected = new List<string>();
        var used = 0;
        foreach (var chunk in chunks)
        {
            var item = $"[{chunk.Kind}: {chunk.Name}] {chunk.Text}";
            if (used + item.Length > maxChars) continue;
            selected.Add(item);
            used += item.Length;
        }
        return string.Join("\n\n", selected);
    }

    private static IEnumerable<string> Chunk(string text)
    {
        text = text.Replace("\r", "").Trim();
        for (var i = 0; i < text.Length; i += 1000)
            yield return text.Substring(i, Math.Min(1200, text.Length - i));
    }

    private static IEnumerable<string> Tokens(string text)
    {
        text = text.ToLowerInvariant();
        for (var i = 0; i < text.Length - 1; i++)
            if (char.IsLetterOrDigit(text[i]) && char.IsLetterOrDigit(text[i + 1]))
                yield return text.Substring(i, 2);
    }
}
