using System.Text.Json;
using ResearchDiscovery.Api.Controllers;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class OtpEventParsingTests
{
    [Fact]
    public void ExtractsRecipientCodeAndRequestType()
    {
        var payload = JsonDocument.Parse("""
            {
              "type": "microsoft.graph.authenticationEvent.emailOtpSend",
              "data": {
                "@odata.type": "microsoft.graph.onOtpSendCalloutData",
                "otpContext": { "identifier": "friend@example.com", "oneTimeCode": "12345678" },
                "authenticationContext": { "requestType": "signUp" }
              }
            }
            """).RootElement;

        Assert.True(AuthEventsController.TryExtractOtp(payload, out var to, out var code, out var type));
        Assert.Equal("friend@example.com", to);
        Assert.Equal("12345678", code);
        Assert.Equal("signUp", type);
    }

    [Fact]
    public void ParsesLowercaseOnetimecode()
    {
        // Microsoft's docs and samples disagree on the casing of oneTimeCode.
        var payload = JsonDocument.Parse("""
            { "data": { "otpContext": { "identifier": "x@y.z", "onetimecode": "999" } } }
            """).RootElement;

        Assert.True(AuthEventsController.TryExtractOtp(payload, out _, out var code, out _));
        Assert.Equal("999", code);
    }

    [Fact]
    public void RejectsPayloadWithoutCode()
    {
        var payload = JsonDocument.Parse("""
            { "data": { "otpContext": { "identifier": "x@y.z" } } }
            """).RootElement;

        Assert.False(AuthEventsController.TryExtractOtp(payload, out _, out _, out _));
    }
}
