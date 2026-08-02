using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Tests.Pipeline;

public sealed class PlaybooksTests
{
    [Fact]
    public void Keys_AreSingleLowercaseWords_AndUnique()
    {
        var keys = Playbooks.All.Select(p => p.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keys, k => Assert.Matches("^[a-z]+$", k));
    }

    [Fact]
    public void Every_Playbook_HasGuidance()
    {
        Assert.All(Playbooks.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.True(p.Guidance.Length > 40, $"{p.Key} guidance too thin");
        });
    }

    [Theory]
    [InlineData("nostalgia", true)]
    [InlineData("NOSTALGIA", true)]
    [InlineData("copycat", true)]
    [InlineData("unicorns", false)]
    [InlineData(null, false)]
    public void TryGet_Works(string? key, bool expected)
    {
        Assert.Equal(expected, Playbooks.TryGet(key, out _));
    }

    [Fact]
    public void Sample_ReturnsDistinct()
    {
        var sample = Playbooks.Sample(2);

        Assert.Equal(2, sample.Count);
        Assert.NotEqual(sample[0].Key, sample[1].Key);
    }
}
