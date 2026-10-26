using System.Text;
using FloatDesk.Core.Translation;

namespace FloatDesk.Core.Tests;

public sealed class TranslationTextProcessorTests
{
    [Fact]
    public void NormalizeConvertsLineEndingsAndCollapsesBlankLines()
    {
        Assert.Equal("first\n\nsecond", TranslationTextProcessor.Normalize("first  \r\n \r\n\r\nsecond \r\n"));
    }

    [Fact]
    public void ChunkingPreservesOrderAndRespectsUtf8Limit()
    {
        var input = string.Concat(Enumerable.Repeat("这是需要翻译的句子。", 80));
        var chunks = TranslationTextProcessor.Chunk(input, 450, 5000);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(Encoding.UTF8.GetByteCount(chunk.Text) <= 450));
        Assert.Equal(input, string.Concat(chunks.Select(c => c.Text + c.SeparatorAfter)));
    }

    [Fact]
    public void ChunkingPreservesParagraphSeparatorsAndCharacterLimit()
    {
        const string input = "First paragraph.\n\nSecond paragraph. Last sentence.";
        var chunks = TranslationTextProcessor.Chunk(input, 100, 22);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 22));
        Assert.Equal(input, string.Concat(chunks.Select(c => c.Text + c.SeparatorAfter)));
    }

    [Fact]
    public void ChunkingDoesNotSplitSurrogatePairs()
    {
        const string input = "😀😀😀";
        var chunks = TranslationTextProcessor.Chunk(input, 4, 2);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal("😀", chunk.Text));
    }

    [Theory]
    [InlineData("aaaaa\n\nbbbbb", 2)]
    [InlineData("aaaaa\n\nbbbbb\n\nccccc", 3)]
    public void ChunkingAttachesStandaloneParagraphDelimiterToPreviousChunk(string input, int expectedCount)
    {
        var chunks = TranslationTextProcessor.Chunk(input, 5, 5);
        Assert.Equal(expectedCount, chunks.Count);
        Assert.All(chunks, chunk => Assert.NotEmpty(chunk.Text));
        Assert.Equal("aaaaa", chunks[0].Text);
        Assert.Equal("\n\n", chunks[0].SeparatorAfter);
        Assert.Equal(input, string.Concat(chunks.Select(c => c.Text + c.SeparatorAfter)));
    }

    [Theory]
    [InlineData("Pay $100", "Pay €100")]
    [InlineData("x + y", "x - y")]
    public void NearDuplicatePreservesMeaningfulSymbols(string previous, string current)
    {
        Assert.False(TranslationTextProcessor.IsNearDuplicate(previous, current, threshold: 1));
    }

    [Fact]
    public void JoinUsesLanguageAppropriateSeparator()
    {
        Assert.Equal("你好世界", TranslationTextProcessor.Join(["你好", "世界"], "zh-Hans"));
        Assert.Equal("hello world", TranslationTextProcessor.Join(["hello", "world"], "en"));
    }

    [Fact]
    public void NearDuplicateDetectsSubtitleRepaintNoise()
    {
        Assert.True(TranslationTextProcessor.IsNearDuplicate("欢迎使用实时字幕功能", "欢迎使用实时字幕功能。"));
        Assert.False(TranslationTextProcessor.IsNearDuplicate("欢迎使用实时字幕功能", "这是一个完全不同的字幕"));
    }
}
