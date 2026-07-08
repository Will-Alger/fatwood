using ResearchDiscovery.Infrastructure.Arxiv;
using Xunit;

namespace ResearchDiscovery.UnitTests.Arxiv;

public class ArxivAtomParserTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parse_RealResponse_ReadsTotalResultsFromOpenSearch()
    {
        var page = ArxivAtomParser.Parse(LoadFixture("arxiv-cs-lg-sample.xml"));

        Assert.Equal(247, page.TotalResults);
        Assert.Equal(5, page.Entries.Count);
    }

    [Fact]
    public void Parse_RealResponse_ExtractsAllFieldsOfFirstEntry()
    {
        var page = ArxivAtomParser.Parse(LoadFixture("arxiv-cs-lg-sample.xml"));
        var entry = page.Entries[0];

        Assert.Equal("2606.09863", entry.ArxivId);
        Assert.Equal(1, entry.Version);
        Assert.Equal(
            "From Confident Closing to Silent Failure: Characterizing False Success in LLM Agents",
            entry.Title);
        Assert.StartsWith("LLM agents can fail silently", entry.Abstract, StringComparison.Ordinal);
        Assert.Equal(["Laksh Advani"], entry.Authors);
        Assert.Equal("cs.LG", entry.PrimaryCategory);
        Assert.Contains("cs.LG", entry.Categories);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 13, 23, TimeSpan.Zero), entry.Published);
        Assert.Equal("https://arxiv.org/abs/2606.09863v1", entry.AbsUrl);
        Assert.Equal("https://arxiv.org/pdf/2606.09863v1", entry.PdfUrl);
        Assert.Null(entry.Doi);
    }

    [Fact]
    public void Parse_RealResponse_HandlesMultipleAuthorsAndCategories()
    {
        var page = ArxivAtomParser.Parse(LoadFixture("arxiv-cs-lg-sample.xml"));
        var entry = page.Entries[1];

        Assert.Equal(4, entry.Authors.Count);
        Assert.Equal("Heng Zhao", entry.Authors[0]);
        Assert.Equal(["cs.LG", "cs.AI"], entry.Categories);
        Assert.Equal("cs.LG", entry.PrimaryCategory);
    }

    [Fact]
    public void Parse_NormalizesWhitespaceInTitleAndAbstract()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:opensearch="http://a9.com/-/spec/opensearch/1.1/" xmlns:arxiv="http://arxiv.org/schemas/atom" xmlns="http://www.w3.org/2005/Atom">
              <opensearch:totalResults>1</opensearch:totalResults>
              <entry>
                <id>http://arxiv.org/abs/2501.00001v3</id>
                <title>A title
                  split   across lines</title>
                <summary>  An abstract
                  with   extra whitespace.  </summary>
                <published>2025-01-01T00:00:00Z</published>
                <updated>2025-01-02T00:00:00Z</updated>
                <category term="cs.SE" scheme="http://arxiv.org/schemas/atom"/>
                <arxiv:primary_category term="cs.SE"/>
                <author><name>Ada Lovelace</name></author>
                <link href="http://arxiv.org/abs/2501.00001v3" rel="alternate" type="text/html"/>
              </entry>
            </feed>
            """;

        var entry = Assert.Single(ArxivAtomParser.Parse(xml).Entries);

        Assert.Equal("A title split across lines", entry.Title);
        Assert.Equal("An abstract with extra whitespace.", entry.Abstract);
        Assert.Equal(3, entry.Version);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), entry.Updated);
        // No pdf link in the entry: derived from the abs URL.
        Assert.Equal("http://arxiv.org/pdf/2501.00001v3", entry.PdfUrl);
    }

    [Fact]
    public void Parse_SupportsOldStyleArxivIdsAndDoi()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:opensearch="http://a9.com/-/spec/opensearch/1.1/" xmlns:arxiv="http://arxiv.org/schemas/atom" xmlns="http://www.w3.org/2005/Atom">
              <opensearch:totalResults>1</opensearch:totalResults>
              <entry>
                <id>http://arxiv.org/abs/math/0309136v2</id>
                <title>Old style identifier</title>
                <summary>Abstract.</summary>
                <published>2003-09-08T00:00:00Z</published>
                <updated>2003-09-09T00:00:00Z</updated>
                <category term="math.CO" scheme="http://arxiv.org/schemas/atom"/>
                <arxiv:primary_category term="math.CO"/>
                <arxiv:doi>10.1000/example.doi</arxiv:doi>
                <author><name>G. H. Hardy</name></author>
                <link href="http://arxiv.org/abs/math/0309136v2" rel="alternate" type="text/html"/>
              </entry>
            </feed>
            """;

        var entry = Assert.Single(ArxivAtomParser.Parse(xml).Entries);

        Assert.Equal("math/0309136", entry.ArxivId);
        Assert.Equal(2, entry.Version);
        Assert.Equal("10.1000/example.doi", entry.Doi);
    }

    [Fact]
    public void Parse_SkipsErrorPseudoEntries()
    {
        // arXiv reports malformed queries as an entry whose id is an
        // api/errors link rather than an abs URL.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:opensearch="http://a9.com/-/spec/opensearch/1.1/" xmlns="http://www.w3.org/2005/Atom">
              <opensearch:totalResults>1</opensearch:totalResults>
              <entry>
                <id>http://arxiv.org/api/errors#incorrect_id_format</id>
                <title>Error</title>
                <summary>incorrect id format</summary>
              </entry>
            </feed>
            """;

        var page = ArxivAtomParser.Parse(xml);

        Assert.Empty(page.Entries);
        Assert.Equal(1, page.TotalResults);
    }

    [Fact]
    public void Parse_NonAtomPayload_Throws()
    {
        Assert.Throws<FormatException>(() => ArxivAtomParser.Parse("<html><body>oops</body></html>"));
    }

    [Fact]
    public void Parse_PrimaryCategoryMissingFromCategoryList_IsPrepended()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:opensearch="http://a9.com/-/spec/opensearch/1.1/" xmlns:arxiv="http://arxiv.org/schemas/atom" xmlns="http://www.w3.org/2005/Atom">
              <opensearch:totalResults>1</opensearch:totalResults>
              <entry>
                <id>http://arxiv.org/abs/2502.11111v1</id>
                <title>Cross listed</title>
                <summary>Abstract.</summary>
                <published>2025-02-01T00:00:00Z</published>
                <updated>2025-02-01T00:00:00Z</updated>
                <category term="stat.ML" scheme="http://arxiv.org/schemas/atom"/>
                <arxiv:primary_category term="cs.LG"/>
                <author><name>Anon</name></author>
                <link href="http://arxiv.org/abs/2502.11111v1" rel="alternate" type="text/html"/>
              </entry>
            </feed>
            """;

        var entry = Assert.Single(ArxivAtomParser.Parse(xml).Entries);

        Assert.Equal("cs.LG", entry.PrimaryCategory);
        Assert.Equal(["cs.LG", "stat.ML"], entry.Categories);
    }
}
