using System.Globalization;
using System.Text;
using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Builds triage prompts. Pure and testable; the rubric IS the product here.</summary>
public static class TriagePrompt
{
    public const string System =
        """
        You extract product-opportunity signals from internet posts. Reply with ONLY a valid JSON object.

        Schema:
        {"relevance":0.0-1.0,"language":"en|other","signals":[{"kind":"pain|wish|demand|trend|complaint","summary":"one concrete sentence, standalone","audience":"who has this problem","commercial_sentiment":"buys_despite_complaints|genuine_need|nice_to_have|no_market","novelty":0.0-1.0,"confidence":0.0-1.0}]}

        Rules:
        - MOST posts contain no signal. Then return {"relevance":<low>,"language":"..","signals":[]}. Never invent signals.
        - A signal is a concrete unmet need, recurring pain, stated purchase intent, or product/service gap.
        - Complaints about products people keep buying are VALID market signals (buys_despite_complaints) - hate does not cancel demand.
        - Ignore politics, war, celebrity drama, memes without product context.
        - Never describe or infer characteristics of individual users; analyze topics only.
        - Each summary must be understandable without reading the post. Max 5 signals.
        """;

    public static string BuildUserMessage(TriageInput input, TriageOptions options)
    {
        var builder = new StringBuilder();
        builder.Append("source: ").Append(input.Source).Append(" · community: ")
            .Append(input.Community ?? "-")
            .Append(" · score: ").Append(input.Score.ToString(CultureInfo.InvariantCulture))
            .Append(" · comments: ").Append(input.CommentCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        builder.Append("title: ").Append(input.Title).Append('\n');

        if (!string.IsNullOrWhiteSpace(input.Body))
        {
            builder.Append("body: ").Append(Truncate(input.Body, options.MaxBodyChars)).Append('\n');
        }

        if (input.Comments.Count > 0)
        {
            builder.Append("top comments:\n");
            foreach (var comment in input.Comments.Take(options.MaxCommentsInPrompt))
            {
                builder.Append("- ").Append(Truncate(comment.Text, options.MaxCommentChars)).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
