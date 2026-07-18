using ResearchDiscovery.Infrastructure.Arxiv;
using Xunit;

namespace ResearchDiscovery.UnitTests.Arxiv;

public class ArxivOaiParserTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parse_RealResponse_ReadsEntriesAndResumptionToken()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));

        // Six <record> elements, one of them deleted.
        Assert.Equal(5, page.Entries.Count);
        Assert.Equal("6941976|1001", page.ResumptionToken);
        Assert.Equal(809, page.CompleteListSize);
    }

    [Fact]
    public void Parse_RealResponse_ExtractsAllFieldsOfFirstEntry()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));
        var entry = page.Entries[0];

        Assert.Equal("1612.06115", entry.ArxivId);
        // OAI metadata carries no version; the bulk flow is add-only so the
        // constant 1 never overwrites a proper version.
        Assert.Equal(1, entry.Version);
        Assert.Equal(
            "Complex Network Tools to Understand the Behavior of Criminality in Urban Areas",
            entry.Title);
        Assert.StartsWith("Complex networks are nowadays employed", entry.Abstract, StringComparison.Ordinal);
        // &#34; entities in the raw XML decode to plain quotes.
        Assert.Contains("\"toolset\"", entry.Abstract, StringComparison.Ordinal);
        Assert.Equal(8, entry.Authors.Count);
        Assert.Equal("Gabriel Spadon", entry.Authors[0]);
        Assert.Equal("Jose F. Rodrigues-Jr", entry.Authors[7]);
        Assert.Equal("cs.SI", entry.PrimaryCategory);
        Assert.Equal(["cs.SI", "physics.soc-ph"], entry.Categories);
        // Dates are date-only in OAI; parsed as UTC midnight.
        Assert.Equal(new DateTimeOffset(2016, 12, 24, 0, 0, 0, TimeSpan.Zero), entry.Published);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), entry.Updated);
        // OAI has no link elements; URLs are derived from the versionless id.
        Assert.Equal("https://arxiv.org/abs/1612.06115", entry.AbsUrl);
        Assert.Equal("https://arxiv.org/pdf/1612.06115", entry.PdfUrl);
        Assert.Equal("10.1007/978-3-319-54978-1_63", entry.Doi);
        Assert.Null(entry.CodeUrl);
    }

    [Fact]
    public void Parse_RealResponse_ExtractsCodeUrlFromAbstract()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));
        var entry = Assert.Single(page.Entries, e => e.ArxivId == "2202.13142");

        Assert.Null(entry.Doi);
        Assert.Equal(["cs.CV"], entry.Categories);
        // "...released at https://github.com/chaofengc/FeMaSR." — trailing dot trimmed.
        Assert.Equal("https://github.com/chaofengc/FeMaSR", entry.CodeUrl);
    }

    [Fact]
    public void Parse_RealResponse_ExtractsCodeUrlFromComments()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));
        var entry = Assert.Single(page.Entries, e => e.ArxivId == "2503.05598");

        Assert.Equal("https://github.com/CEADpx/neural_operators", entry.CodeUrl);
        Assert.Equal("10.3390/math14132421", entry.Doi);
    }

    [Fact]
    public void Parse_RealResponse_FormatsKeynameOnlyAuthors()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));
        var entry = Assert.Single(page.Entries, e => e.ArxivId == "2606.10062");

        // The first two authors have no <forenames> element.
        Assert.Equal("Lei", entry.Authors[0]);
        Assert.Equal("Chen", entry.Authors[1]);
        Assert.Equal("Guilin Zhang", entry.Authors[2]);
    }

    [Fact]
    public void Parse_RealResponse_SkipsDeletedRecords()
    {
        var page = ArxivOaiParser.Parse(LoadFixture("arxiv-oai-cs-sample.xml"));

        Assert.DoesNotContain(page.Entries, e => e.ArxivId == "2401.00000");
    }

    [Fact]
    public void Parse_NoUpdatedElement_FallsBackToCreated()
    {
        // A paper that was never revised has <created> but no <updated>.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <OAI-PMH xmlns="http://www.openarchives.org/OAI/2.0/">
              <responseDate>2026-07-17T00:00:00Z</responseDate>
              <request verb="ListRecords">http://oaipmh.arxiv.org/oai</request>
              <ListRecords>
                <record>
                  <header>
                    <identifier>oai:arXiv.org:2501.00001</identifier>
                    <datestamp>2025-01-05</datestamp>
                    <setSpec>cs:cs:LG</setSpec>
                  </header>
                  <metadata>
                    <arXiv xmlns="http://arxiv.org/OAI/arXiv/">
                      <id>2501.00001</id>
                      <created>2025-01-01</created>
                      <authors><author><keyname>Lovelace</keyname><forenames>Ada</forenames></author></authors>
                      <title>A title
                        split   across lines</title>
                      <categories>cs.LG stat.ML</categories>
                      <abstract>  An abstract
                        with   extra whitespace.  </abstract>
                    </arXiv>
                  </metadata>
                </record>
              </ListRecords>
            </OAI-PMH>
            """;

        var page = ArxivOaiParser.Parse(xml);
        var entry = Assert.Single(page.Entries);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), entry.Published);
        Assert.Equal(entry.Published, entry.Updated);
        Assert.Equal("A title split across lines", entry.Title);
        Assert.Equal("An abstract with extra whitespace.", entry.Abstract);
        Assert.Equal("cs.LG", entry.PrimaryCategory);
        Assert.Equal(["cs.LG", "stat.ML"], entry.Categories);
        // No token element at all → final page.
        Assert.Null(page.ResumptionToken);
        Assert.Null(page.CompleteListSize);
    }

    [Fact]
    public void Parse_MultipleSpaceSeparatedDois_TakesFirst()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <OAI-PMH xmlns="http://www.openarchives.org/OAI/2.0/">
              <responseDate>2026-07-17T00:00:00Z</responseDate>
              <request verb="ListRecords">http://oaipmh.arxiv.org/oai</request>
              <ListRecords>
                <record>
                  <header>
                    <identifier>oai:arXiv.org:2501.00002</identifier>
                    <datestamp>2025-01-05</datestamp>
                    <setSpec>cs:cs:LG</setSpec>
                  </header>
                  <metadata>
                    <arXiv xmlns="http://arxiv.org/OAI/arXiv/">
                      <id>2501.00002</id>
                      <created>2025-01-02</created>
                      <authors><author><keyname>Hopper</keyname><forenames>Grace</forenames></author></authors>
                      <title>Two DOIs</title>
                      <categories>cs.LG</categories>
                      <doi>10.1000/first 10.1000/second</doi>
                      <abstract>Abstract.</abstract>
                    </arXiv>
                  </metadata>
                </record>
                <resumptionToken cursor="0" completeListSize="42"></resumptionToken>
              </ListRecords>
            </OAI-PMH>
            """;

        var page = ArxivOaiParser.Parse(xml);
        var entry = Assert.Single(page.Entries);

        Assert.Equal("10.1000/first", entry.Doi);
        // An empty token element is how the last page of a resumed list looks.
        Assert.Null(page.ResumptionToken);
        Assert.Equal(42, page.CompleteListSize);
    }

    [Fact]
    public void Parse_NoRecordsMatchError_ReturnsEmptyPage()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <OAI-PMH xmlns="http://www.openarchives.org/OAI/2.0/">
              <responseDate>2026-07-17T00:00:00Z</responseDate>
              <request verb="ListRecords">http://oaipmh.arxiv.org/oai</request>
              <error code="noRecordsMatch">No matching records</error>
            </OAI-PMH>
            """;

        var page = ArxivOaiParser.Parse(xml);

        Assert.Empty(page.Entries);
        Assert.Null(page.ResumptionToken);
        Assert.Null(page.CompleteListSize);
    }

    [Fact]
    public void Parse_OtherOaiError_ThrowsWithCode()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <OAI-PMH xmlns="http://www.openarchives.org/OAI/2.0/">
              <responseDate>2026-07-17T00:00:00Z</responseDate>
              <request verb="ListRecords">http://oaipmh.arxiv.org/oai</request>
              <error code="badResumptionToken">Token expired</error>
            </OAI-PMH>
            """;

        var ex = Assert.Throws<FormatException>(() => ArxivOaiParser.Parse(xml));

        Assert.Contains("badResumptionToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NonOaiPayload_Throws()
    {
        Assert.Throws<FormatException>(() => ArxivOaiParser.Parse("<html><body>oops</body></html>"));
    }
}
