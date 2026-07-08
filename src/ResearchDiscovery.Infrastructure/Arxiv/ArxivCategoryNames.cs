namespace ResearchDiscovery.Infrastructure.Arxiv;

/// <summary>
/// Static reference map from arXiv category codes to display names, taken from
/// the published arXiv category taxonomy (https://arxiv.org/category_taxonomy).
/// This is reference data about arXiv's own taxonomy — NOT seeded product
/// data; category rows are only created when ingestion actually encounters or
/// targets a code, and unknown codes simply fall back to the code itself.
/// </summary>
public static class ArxivCategoryNames
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cs.AI"] = "Artificial Intelligence",
            ["cs.CL"] = "Computation and Language",
            ["cs.CR"] = "Cryptography and Security",
            ["cs.CV"] = "Computer Vision and Pattern Recognition",
            ["cs.CY"] = "Computers and Society",
            ["cs.DB"] = "Databases",
            ["cs.DC"] = "Distributed, Parallel, and Cluster Computing",
            ["cs.DS"] = "Data Structures and Algorithms",
            ["cs.GT"] = "Computer Science and Game Theory",
            ["cs.HC"] = "Human-Computer Interaction",
            ["cs.IR"] = "Information Retrieval",
            ["cs.IT"] = "Information Theory",
            ["cs.LG"] = "Machine Learning",
            ["cs.NE"] = "Neural and Evolutionary Computing",
            ["cs.NI"] = "Networking and Internet Architecture",
            ["cs.PL"] = "Programming Languages",
            ["cs.RO"] = "Robotics",
            ["cs.SD"] = "Sound",
            ["cs.SE"] = "Software Engineering",
            ["econ.EM"] = "Econometrics",
            ["eess.AS"] = "Audio and Speech Processing",
            ["eess.IV"] = "Image and Video Processing",
            ["eess.SP"] = "Signal Processing",
            ["eess.SY"] = "Systems and Control",
            ["math.OC"] = "Optimization and Control",
            ["math.PR"] = "Probability",
            ["math.ST"] = "Statistics Theory",
            ["q-fin.CP"] = "Computational Finance",
            ["q-fin.EC"] = "Economics",
            ["q-fin.GN"] = "General Finance",
            ["q-fin.MF"] = "Mathematical Finance",
            ["q-fin.PM"] = "Portfolio Management",
            ["q-fin.PR"] = "Pricing of Securities",
            ["q-fin.RM"] = "Risk Management",
            ["q-fin.ST"] = "Statistical Finance",
            ["q-fin.TR"] = "Trading and Market Microstructure",
            ["stat.AP"] = "Applications",
            ["stat.CO"] = "Computation",
            ["stat.ME"] = "Methodology",
            ["stat.ML"] = "Machine Learning (Statistics)",
        };

    public static string DisplayNameFor(string code) =>
        Names.TryGetValue(code, out var name) ? name : code;
}
