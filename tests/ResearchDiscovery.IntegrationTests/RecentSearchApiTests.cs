using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Domain.Entities;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Recent-search history and exact replay (/api/me/searches). Replay must
/// reproduce the logged order, ids, and scores without re-running the ranker,
/// and must never expose another account's searches.
/// </summary>
public class RecentSearchApiTests
{
    [Fact]
    public async Task Replay_ReturnsLoggedOrderIdsAndScoresExactly()
    {
        using var factory = new ApiFactory { TestUserExternalId = "history-member" };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        // First authenticated call provisions the member; grab their id.
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        var userId = me.GetProperty("id").GetInt64();

        long eventId = await SeedSearchEventAsync(factory, userId, [
            ("2501.00003", 1, 0.91f, false),
            ("2501.00001", 2, 0.77f, false),
            ("2501.00002", 3, 0.55f, true),
        ]);

        // Listed in history.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/me/searches");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(3, list[0].GetProperty("resultCount").GetInt32());
        Assert.Equal(eventId, list[0].GetProperty("searchEventId").GetInt64());

        // Replayed to the exact slots, in rank order, with the stored scores.
        var replay = await client.GetFromJsonAsync<JsonElement>($"/api/me/searches/{eventId}");
        var hits = replay.GetProperty("hits");
        Assert.Equal(3, hits.GetArrayLength());
        Assert.Equal("2501.00003", hits[0].GetProperty("paper").GetProperty("arxivId").GetString());
        Assert.Equal("2501.00001", hits[1].GetProperty("paper").GetProperty("arxivId").GetString());
        Assert.Equal("2501.00002", hits[2].GetProperty("paper").GetProperty("arxivId").GetString());
        Assert.Equal(0.91f, hits[0].GetProperty("matchScore").GetSingle(), 3);
        Assert.True(hits[2].GetProperty("isWildcard").GetBoolean());
        Assert.Equal("You want anomaly detection projects.",
            replay.GetProperty("plan").GetProperty("interpretation").GetString());
    }

    [Fact]
    public async Task Replay_OtherUsersSearch_Returns404()
    {
        using var factory = new ApiFactory { TestUserExternalId = "nosy-member" };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        await client.GetFromJsonAsync<JsonElement>("/api/me"); // provision caller

        // A search owned by some OTHER account (id 99999).
        long othersEvent = await SeedSearchEventAsync(factory, 99999, [
            ("2501.00001", 1, 0.8f, false),
        ]);

        var response = await client.GetAsync($"/api/me/searches/{othersEvent}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<long> SeedSearchEventAsync(
        ApiFactory factory, long userId,
        (string ArxivId, int Rank, float Score, bool IsWildcard)[] slots)
    {
        long eventId = 0;
        await factory.SeedAsync(async db =>
        {
            var idByArxiv = await db.Papers
                .ToDictionaryAsync(p => p.ArxivId, p => p.Id, StringComparer.Ordinal);

            var evt = new SearchEvent
            {
                UserId = userId,
                CreatedUtc = DateTimeOffset.UtcNow,
                QueryText = "anomaly detection projects",
                PlanJson = """
                {"interpretation":"You want anomaly detection projects.",
                 "anchorText":"anomaly detection, outlier detection",
                 "categories":["cs.LG"],"dateWindowDays":null,"requireNoCode":null}
                """,
                TotalCandidates = 42,
                ResultLimit = slots.Length,
            };
            foreach (var s in slots)
            {
                evt.Results.Add(new SearchEventResult
                {
                    Rank = s.Rank,
                    PaperId = idByArxiv[s.ArxivId],
                    Score = s.Score,
                    IsWildcard = s.IsWildcard,
                });
            }

            db.SearchEvents.Add(evt);
            await db.SaveChangesAsync();
            eventId = evt.Id;
        });
        return eventId;
    }
}
