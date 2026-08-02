namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>A product-opportunity signal extracted from one raw item by triage.</summary>
public sealed class SignalEntity
{
    public long Id { get; set; }

    public long RawItemId { get; set; }

    public RawItemEntity? RawItem { get; set; }

    /// <summary>pain | wish | demand | trend | complaint.</summary>
    public required string Kind { get; set; }

    /// <summary>One concrete sentence, understandable without the source post.</summary>
    public required string Summary { get; set; }

    /// <summary>≤10-word glance line for rankings; generated lazily by the cheapest model, cached.</summary>
    public string? Glance { get; set; }

    public string? Audience { get; set; }

    /// <summary>buys_despite_complaints | genuine_need | nice_to_have | no_market.</summary>
    public required string CommercialSentiment { get; set; }

    public double Novelty { get; set; }

    public double Confidence { get; set; }

    public required string Model { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
