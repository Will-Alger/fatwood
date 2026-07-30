using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Output-size knobs for the query compiler's structured-output schema.
/// Compile wall-time is roughly proportional to output tokens, and the
/// anchor-topic list plus the HyDE abstract dominate them. Defaults exactly
/// reproduce the shipped prompt; any change is a ranking change and goes
/// through the eval protocol (nDCG via `eval search`, category F1 via
/// `eval categories`) before it may become the default.
/// </summary>
public class SearchCompilerOptions
{
    public const string SectionName = "Search:Compiler";

    [Range(1, 30)]
    public int MinTopics { get; set; } = 8;

    [Range(1, 30)]
    public int MaxTopics { get; set; } = 15;

    [Range(1, 10)]
    public int HydeMinSentences { get; set; } = 4;

    [Range(1, 10)]
    public int HydeMaxSentences { get; set; } = 6;
}
