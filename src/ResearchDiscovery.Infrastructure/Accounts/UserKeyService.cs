using Anthropic;
using Anthropic.Models.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Accounts;

public class UserKeyService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    AnthropicClient baseClient,
    ILogger<UserKeyService> logger) : IUserKeyService
{
    // Purpose string is part of the encryption context — never change it or
    // existing ciphertexts stop decrypting.
    private const string ProtectorPurpose = "Fatwood.UserAnthropicKeys.v1";

    public async Task<string> SetAsync(long userId, string apiKey, CancellationToken ct)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length is < 20 or > 256 || !apiKey.StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            throw new InvalidUserKeyException(
                "That doesn't look like an Anthropic API key (they start with sk-ant-).");
        }

        // Cheap live validation: the models list endpoint is free and fails
        // fast on a bad key. The key itself must never appear in logs.
        try
        {
            var probe = baseClient.WithOptions(options => options with { ApiKey = apiKey });
            await probe.Models.List(new ModelListParams { Limit = 1 }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("BYO key validation failed for user {UserId}", userId);
            throw new InvalidUserKeyException(
                "Anthropic rejected that key. Check it in the Anthropic console and try again.");
        }

        var last4 = apiKey[^4..];
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var encrypted = protector.Protect(apiKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AppUsers
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.EncryptedAnthropicKey, encrypted)
                .SetProperty(u => u.AnthropicKeyLast4, last4)
                .SetProperty(u => u.AnthropicKeySetUtc, DateTimeOffset.UtcNow), ct);

        logger.LogInformation("User {UserId} set a BYO Anthropic key (…{Last4})", userId, last4);
        return last4;
    }

    public async Task RemoveAsync(long userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AppUsers
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.EncryptedAnthropicKey, (string?)null)
                .SetProperty(u => u.AnthropicKeyLast4, (string?)null)
                .SetProperty(u => u.AnthropicKeySetUtc, (DateTimeOffset?)null), ct);

        logger.LogInformation("User {UserId} removed their BYO Anthropic key", userId);
    }

    public async Task<string?> GetDecryptedAsync(long userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var encrypted = await db.AppUsers
            .Where(u => u.Id == userId)
            .Select(u => u.EncryptedAnthropicKey)
            .FirstOrDefaultAsync(ct);

        if (encrypted is null)
        {
            return null;
        }

        try
        {
            return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            // Key-ring loss (e.g. wiped DB table) makes old ciphertexts
            // unreadable; treat as "no key" so the platform key takes over.
            logger.LogError(ex,
                "Could not decrypt stored key for user {UserId}; falling back to platform key",
                userId);
            return null;
        }
    }
}
