using System.IO.Compression;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantDocumentImporterTests
{
    [Fact]
    public async Task NearLimitTextCanBeStoredWithOverlappingChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Large-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "large.txt"); await File.WriteAllTextAsync(path, new string('a', 1_990_000));
            var imported = await AssistantDocumentImporter.LoadAsync(path, null, "deny", CancellationToken.None);
            Assert.True(imported.Passages.Sum(chunk => chunk.Content.Length) > 2_000_000);
            using var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            await store.SaveDocumentAsync(imported.Document, imported.Passages, CancellationToken.None);
            Assert.Single(await store.ListDocumentsAsync(null, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PdfTextHasPageSourcesAndDocxCannotReadExternalEntities()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Pdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica); page.AddText("ProjectBlue unique reference", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
            var pdf = Path.Combine(root, "source.pdf"); await File.WriteAllBytesAsync(pdf, builder.Build());
            var imported = await AssistantDocumentImporter.LoadAsync(pdf, null, "deny", CancellationToken.None);
            Assert.Contains(imported.Passages, chunk => chunk.Locator.Contains("第1页", StringComparison.Ordinal) && chunk.Content.Contains("ProjectBlue", StringComparison.Ordinal));
            var docx = Path.Combine(root, "bad.docx");
            using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); await writer.WriteAsync("<!DOCTYPE doc [<!ENTITY data SYSTEM 'file:///C:/private-marker.txt'>]><doc>&data;</doc>"); }
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => AssistantDocumentImporter.LoadAsync(docx, null, "deny", CancellationToken.None)); Assert.DoesNotContain("private-marker", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MarkdownAndDocxBecomeTraceableChunksWithoutRunningCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var md = Path.Combine(root, "facts.md"); await File.WriteAllTextAsync(md, "# 说明\n蓝色主题\n" + new string('字', 3600));
            var imported = await AssistantDocumentImporter.LoadAsync(md, null, "deny", CancellationToken.None);
            Assert.True(imported.Passages.Count >= 3); Assert.All(imported.Passages, passage => Assert.InRange(passage.Content.Length, 1, 1600)); Assert.Equal(64, imported.Document.Sha256.Length);
            var docx = Path.Combine(root, "sample.docx");
            using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); await writer.WriteAsync("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>来自DOCX的文本</w:t></w:r></w:p></w:body></w:document>"); }
            var parsed = await AssistantDocumentImporter.LoadAsync(docx, null, "ask", CancellationToken.None);
            Assert.Contains(parsed.Passages, chunk => chunk.Content.Contains("来自DOCX的文本", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
