using IdeaEngine.Core.Common;
using IdeaEngine.Infrastructure.Autopilot;

namespace IdeaEngine.Tests.Common;

public sealed class GlassPipelineTests
{
    [Fact]
    public void MessageChunker_ShortText_SingleChunk()
    {
        var chunks = MessageChunker.Split("hello world").ToList();
        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void MessageChunker_LongText_SplitsAtLines_NothingLost()
    {
        var lines = Enumerable.Range(1, 300).Select(i => $"line {i} " + new string('x', 40)).ToList();
        var text = string.Join('\n', lines);

        var chunks = MessageChunker.Split(text).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= MessageChunker.ChunkLimit));
        // Every line survives, none is cut in the middle.
        var reassembled = string.Join('\n', chunks);
        Assert.All(lines, line => Assert.Contains(line, reassembled, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sessions_per_day", "5", null)]
    [InlineData("sessions_per_day", "abc", "not a valid integer")]
    [InlineData("min_rating_for_research", "0.45", null)]
    [InlineData("min_rating_for_research", "1.5", "must be a number between 0 and 1 (exclusive)")]
    [InlineData("ideation_time", "09:30", null)]
    [InlineData("ideation_time", "25:99", "must be HH:mm (24h)")]
    public void SettingsCatalog_Validation(string key, string value, string? expectedProblem)
    {
        var spec = SettingsCatalog.Find(key);
        Assert.NotNull(spec);
        Assert.Equal(expectedProblem, SettingsCatalog.Validate(spec!, value));
    }

    [Fact]
    public void SettingsCatalog_UnknownKey_NotFound()
    {
        Assert.Null(SettingsCatalog.Find("rm_rf_slash"));
    }
}
