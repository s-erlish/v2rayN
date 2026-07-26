namespace v2rayN.Desktop.Account.Dto;

// Payment endpoints of the Departament backend. Ported 1:1 from V2rayNG auth/dto/PaymentDtos.kt.
//
//  POST /client/payments/platega -> PaymentInitDto
//  POST /client/payments/balance -> PaymentResultDto
//  GET  /client/payments         -> PaymentsDto

/// <summary>
/// Body for POST /client/payments/platega and /client/payments/balance. All fields optional — the
/// caller fills only what a given purchase needs (null fields are omitted on the wire).
/// </summary>
public sealed class PaymentRequestDto
{
    public double? Amount { get; set; }
    public string? Currency { get; set; }
    public string? TariffId { get; set; }
    public string? TariffPriceOptionId { get; set; }

    /// <summary>Extra devices for a BALANCE purchase/renewal (POST /client/payments/balance).</summary>
    public int? DeviceCount { get; set; }

    /// <summary>Extra devices for a scoped CARD renewal (POST /client/payments/tariff/platega uses
    /// `extraDevices`, not `deviceCount`).</summary>
    public int? ExtraDevices { get; set; }

    public int? PaymentMethod { get; set; }
    public string? PromoCode { get; set; }
    public string? SubscriptionUuid { get; set; }

    // Scoped renewal: address a specific (root or secondary) subscription. Both null → legacy root
    // behavior (balance) / required together for /payments/tariff/platega. scope is "root"|"secondary";
    // subscriptionId is the client id for root, else the secondary subscription id.
    public string? Scope { get; set; }
    public string? SubscriptionId { get; set; }
}

/// <summary>Returned when a payment provider checkout URL is issued (Platega, add-devices, upgrade).</summary>
public sealed class PaymentInitDto
{
    public string PaymentUrl { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string OrderId { get; set; } = "";
}

/// <summary>Returned by a balance (wallet) payment that settles immediately. The Departament backend's
/// tariff-purchase/renewal reply is {message, paymentId, newBalance}; Status/OrderId are kept for
/// source compatibility with older callers and stay blank on this endpoint.</summary>
public sealed class PaymentResultDto
{
    public string Status { get; set; } = "";
    public string OrderId { get; set; } = "";

    // Actual fields returned by POST /client/payments/balance for a tariff purchase/renewal.
    public string? Message { get; set; }
    public string? PaymentId { get; set; }
    public double? NewBalance { get; set; }
}

/// <summary>GET /client/payments</summary>
public sealed class PaymentsDto
{
    public List<PaymentDto> Items { get; set; } = new();
}

/// <summary>A single payment/order history entry.</summary>
public sealed class PaymentDto
{
    public string Id { get; set; } = "";
    public string OrderId { get; set; } = "";
    public double Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
