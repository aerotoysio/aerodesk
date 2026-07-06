using AeroDesk.Core.Domain;

namespace AeroDesk.Core.Retailing;

public sealed record PaymentAuthorization(bool Approved, string AuthorizationCode, string? DeclineReason);

/// <summary>
/// Payment seam. AeroDesk is a demo: the only implementation is a mock that
/// returns a synthetic authorization code. PCI-DSS NOTE: production must use a
/// compliant hosted/tokenized gateway (hosted fields or redirect); agents must
/// never read, enter, or store full card numbers or CVVs, and this codebase
/// must never carry a PAN — only opaque tokens and last-4 digits.
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentAuthorization> AuthorizeAsync(PaymentToken token, PriceDetail amount, CancellationToken ct = default);
}

/// <summary>Approves everything with a deterministic-looking auth code. Demo only.</summary>
public sealed class MockPaymentGateway : IPaymentGateway
{
    public Task<PaymentAuthorization> AuthorizeAsync(PaymentToken token, PriceDetail amount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token.Token))
            return Task.FromResult(new PaymentAuthorization(false, "", "Missing payment token."));
        var code = $"AUTH-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        return Task.FromResult(new PaymentAuthorization(true, code, null));
    }
}
