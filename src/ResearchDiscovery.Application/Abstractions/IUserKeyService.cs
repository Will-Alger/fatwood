namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Write-only management of a user's own Anthropic API key. The key is
/// encrypted at rest, never logged, and never returned by any endpoint —
/// only its last 4 characters are echoed for recognition. The server
/// necessarily decrypts it to call Anthropic on the user's behalf.
/// </summary>
public interface IUserKeyService
{
    /// <summary>
    /// Validates the key against the Anthropic API (a free models-list call)
    /// and stores it encrypted. Returns the last-4 fragment on success.
    /// </summary>
    /// <exception cref="InvalidUserKeyException">Key malformed or rejected by Anthropic.</exception>
    Task<string> SetAsync(long userId, string apiKey, CancellationToken ct);

    Task RemoveAsync(long userId, CancellationToken ct);

    /// <summary>Decrypted key for API calls; null when the user has none. Never log the result.</summary>
    Task<string?> GetDecryptedAsync(long userId, CancellationToken ct);
}

public class InvalidUserKeyException(string message) : Exception(message);
