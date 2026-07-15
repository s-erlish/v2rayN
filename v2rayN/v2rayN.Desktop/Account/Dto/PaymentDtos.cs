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
    public int? DeviceCount { get; set; }
    public int? PaymentMethod { get; set; }
    public string? PromoCode { get; set; }
    public string? SubscriptionUuid { get; set; }
}

/// <summary>Returned when a payment provider checkout URL is issued (Platega, add-devices, upgrade).</summary>
public sealed class PaymentInitDto
{
    public string PaymentUrl { get; set; } = "";
    public string PaymentId { get; set; } = "";
    public string OrderId { get; set; } = "";
}

/// <summary>Returned by a balance (wallet) payment that settles immediately.</summary>
public sealed class PaymentResultDto
{
    public string Status { get; set; } = "";
    public string OrderId { get; set; } = "";
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
