namespace AeroDesk.Core.Domain;

public enum TripType { OneWay, Return, MultiCity }

public enum Cabin { Economy, PremiumEconomy, Business, First }

/// <summary>Passenger type code (IATA PTC).</summary>
public enum Ptc { ADT, CHD, INF }

public enum OrderStatus { Draft, PendingPayment, Paid, Ticketed, Cancelled }

/// <summary>Base fare + taxes/fees in a single currency.</summary>
public sealed record PriceDetail(decimal BaseAmount, decimal Taxes, string Currency)
{
    public decimal Total => BaseAmount + Taxes;

    public static PriceDetail Zero(string currency) => new(0m, 0m, currency);

    public static PriceDetail Sum(IEnumerable<PriceDetail> prices, string currency)
    {
        decimal baseAmount = 0m, taxes = 0m;
        foreach (var p in prices)
        {
            if (!string.Equals(p.Currency, currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cannot sum {p.Currency} into {currency}.");
            baseAmount += p.BaseAmount;
            taxes += p.Taxes;
        }
        return new PriceDetail(baseAmount, taxes, currency);
    }
}

/// <summary>An expiry attached to an offer (price guarantee) or an order (payment deadline).</summary>
public sealed record TimeLimit(string Kind, DateTime ExpiresAtUtc)
{
    public const string OfferExpiry = "OfferExpiry";
    public const string PaymentDeadline = "PaymentDeadline";
    public const string PriceGuarantee = "PriceGuarantee";

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
}

/// <summary>The logged-in agent + agency (local demo profile — no identity provider).</summary>
public sealed record AgentContext(string AgentName, string AgencyName, string AgencyIata);
