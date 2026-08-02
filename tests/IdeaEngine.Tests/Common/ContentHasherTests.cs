using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class ContentHasherTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var first = ContentHasher.Compute("My drone build log", "Part 3: the flight controller");
        var second = ContentHasher.Compute("My drone build log", "Part 3: the flight controller");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_NormalizesWhitespaceAndCase()
    {
        var messy = ContentHasher.Compute("  LED   Earrings\tfor  COSPLAY ", "does  THIS\nexist?");
        var clean = ContentHasher.Compute("led earrings for cosplay", "does this exist?");

        Assert.Equal(clean, messy);
    }

    [Fact]
    public void Compute_DifferentContent_ProducesDifferentHashes()
    {
        var first = ContentHasher.Compute("3d printed cable organizer");
        var second = ContentHasher.Compute("3d printed cable holder");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_NullBody_EqualsEmptyBody()
    {
        var withNull = ContentHasher.Compute("title only", null);
        var withEmpty = ContentHasher.Compute("title only", string.Empty);

        Assert.Equal(withEmpty, withNull);
    }

    [Fact]
    public void Compute_Returns64CharLowercaseHex()
    {
        var hash = ContentHasher.Compute("anything");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Compute_NullTitle_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ContentHasher.Compute(null!));
    }
}
