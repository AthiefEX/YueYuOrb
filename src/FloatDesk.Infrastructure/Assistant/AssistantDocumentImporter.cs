using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using FloatDesk.Core.Assistant;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FloatDesk.Infrastructure.Assistant;

public sealed record AssistantImportedDocument(AssistantDocument Document, IReadOnlyList<AssistantPassage> Passages)
{ public string ExtractedText { get; init; } = ""; }

public static class AssistantDocumentImporter
{
    private static readonly HashSet<string> TextExtensions = new([".txt", ".md", ".markdown", ".cs", ".json", ".yaml", ".yml", ".py", ".js", ".ts", ".tsx", ".jsx", ".cpp", ".c", ".h", ".html", ".css", ".ps1", ".sql", ".xml", ".ini", ".rs", ".java", ".toml", ".csv"], StringComparer.OrdinalIgnoreCase);
    public static async Task<AssistantImportedDocument> LoadAsync(string path, Guid? project, string cloudPolicy, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("知识文档不能来自文件或目录链接。");
        byte[] bytes;
        using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
        {
            if (file.Length is < 1 or > 20 * 1024 * 1024) throw new InvalidDataException("文档应为1字节至20MiB。");
            bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        try { return await Task.Run(() => Parse(full, bytes, project, cloudPolicy, token), token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException and not InvalidDataException)
        { throw new InvalidDataException("文档无法解析，请检查格式、加密状态或先转换为TXT。", error); }
    }

    private static AssistantImportedDocument Parse(string path, byte[] bytes, Guid? project, string cloudPolicy, CancellationToken token)
    {
        if (cloudPolicy is not ("deny" or "ask" or "allow")) throw new ArgumentException("云端策略无效。");
        var document = new AssistantDocument(Guid.NewGuid(), project, Path.GetFileName(path), path, Convert.ToHexString(SHA256.HashData(bytes)), true, cloudPolicy, DateTimeOffset.UtcNow);
        var passages = new List<AssistantPassage>(); var total = 0; var extracted = new StringBuilder();
        void Add(string text, string locator)
        {
            token.ThrowIfCancellationRequested(); text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            total += text.Length; if (total > 2000000) throw new InvalidDataException("文档文字超过200万字符。");
            extracted.Append(text).Append('\n');
            for (var start = 0; start < text.Length;)
            {
                var count = Math.Min(1500, text.Length - start); if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1])) count--;
                var content = text.Substring(start, count).Trim();
                if (content.Length > 0) passages.Add(new(Guid.NewGuid(), document.Id, passages.Count, locator + " · 块" + (passages.Count + 1), content));
                if (passages.Count > 2000) throw new InvalidDataException("文档分块超过2000段。");
                if (start + count >= text.Length) break;
                start += Math.Max(1, count - 120); if (start > 0 && char.IsLowSurrogate(text[start])) start++;
            }
        }
        var extension = Path.GetExtension(path);
        if (TextExtensions.Contains(extension)) Add(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'), "正文");
        else if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new MemoryStream(bytes, writable: false); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX缺少正文。");
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("DOCX正文超过8MiB。");
            using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            var text = new StringBuilder(); var paragraph = 1; var inText = false;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") inText = true;
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && inText) text.Append(reader.Value);
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "t") inText = false;
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") text.Append('\t');
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") { Add(text.ToString(), "段" + paragraph++); text.Clear(); }
            }
            if (text.Length > 0) Add(text.ToString(), "段" + paragraph);
        }
        else if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > 400) throw new InvalidDataException("PDF超过400页，请拆分后导入。");
            foreach (var page in pdf.GetPages()) { token.ThrowIfCancellationRequested(); Add(ContentOrderTextExtractor.GetText(page), "第" + page.Number + "页"); }
        }
        else throw new InvalidDataException("不支持该格式，请使用TXT、Markdown、代码、DOCX或PDF文字文档。");
        if (passages.Count == 0) throw new InvalidDataException("文档没有可提取文字，扫描PDF请先进行OCR再导入。");
        return new(document, passages) { ExtractedText = extracted.ToString() };
    }
}
