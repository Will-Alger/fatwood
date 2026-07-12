using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResearchDiscovery.Domain.Entities;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Account provisioning, the invite-code signup gate, and per-user theme
/// persistence — the /api/me surface.
/// </summary>
public class AccountApiTests
{
    [Fact]
    public async Task Me_AsDevAdmin_IsUnlimited()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("Owner", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("isActive").GetBoolean());
        Assert.True(me.GetProperty("budget").GetProperty("unlimited").GetBoolean());
    }

    [Fact]
    public async Task InviteGate_RedeemActivatesAccount()
    {
        using var factory = new ApiFactory
        {
            TestUserExternalId = "gated-member",
            RequireInviteCode = true,
        };
        await factory.SeedAsync(db =>
        {
            db.InviteCodes.Add(new InviteCode
            {
                Code = "EMBER-42",
                MaxUses = 2,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });
        using var client = factory.CreateClient();

        // Fresh gated account: signed in but inactive.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(before.GetProperty("isActive").GetBoolean());

        // A bad code is rejected; the real one activates.
        var bad = await client.PostAsJsonAsync("/api/me/invite", new { code = "WRONG" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await client.PostAsJsonAsync("/api/me/invite", new { code = "EMBER-42" });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.True(after.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Theme_RoundTripsThroughAccount()
    {
        using var factory = new ApiFactory { TestUserExternalId = "themed-member" };
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/me/theme", new { theme = "light" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("light", me.GetProperty("theme").GetString());

        var invalid = await client.PutAsJsonAsync("/api/me/theme", new { theme = "sepia" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Me_Anonymous_Returns401WhenAuthEnabled()
    {
        // The one place we can assert the real-JWT posture without a tenant:
        // enabling Auth makes bearer the only scheme, so a tokenless request
        // is 401, not silently the dev admin.
        using var factory = new AuthEnabledApiFactory();
        using var client = factory.CreateClient();

        var me = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private sealed class AuthEnabledApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(
            Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Authority", "https://example.ciamlogin.com/tenant/v2.0");
            builder.UseSetting("Auth:Audience", "test-audience");
        }
    }
}
