using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Mcp2Dialog.Server;

/// <summary>
/// Turns <see cref="DialogState"/> into an opaque <c>requestState</c> string and back. The client must echo
/// requestState unchanged, so the server encrypts it and puts an expiry on it. That way the client can't read
/// or tamper with the state, and a stale token can't be replayed indefinitely.
/// </summary>
public sealed class DialogStateProtector(IDataProtectionProvider provider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Mcp2Dialog.DialogState.v1").ToTimeLimitedDataProtector();

    public string Protect(DialogState state) =>
        _protector.Protect(JsonSerializer.Serialize(state), Lifetime);

    /// <param name="requestState">The token the client echoed back.</param>
    /// <param name="state">The decrypted state, if it was valid.</param>
    /// <param name="failure">Why the token was rejected (for example, its key isn't in this instance's key ring).</param>
    public bool TryUnprotect(string requestState, out DialogState? state, out string? failure)
    {
        try
        {
            state = JsonSerializer.Deserialize<DialogState>(_protector.Unprotect(requestState));
            failure = state is null ? "the token held no dialog state" : null;
            return state is not null;
        }
        catch (CryptographicException ex)
        {
            state = null;
            failure = ex.Message;
            return false;
        }
    }
}
