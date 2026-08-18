using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>
/// GET /client/payments.
///
/// The envelope carries alternates for the same reason <see cref="DevicesDto"/> does, and this
/// endpoint had never been given the treatment on the desktop side: reading a flat `items` is right
/// only while the backend happens to name it `items`, and every other name yields an EMPTY history
/// with no error to explain it — the exact failure the Devices screen shipped with once. The Android
/// client already carries this fix against the same backend (auth/dto/PaymentDtos.kt).
///
/// <see cref="Items"/> is what callers read, so the shape stops being their problem. It resolves at
/// READ time rather than funnelling into a field during parsing, which makes it independent of the
/// order the keys happen to arrive in, and lets a non-empty list win over an empty one when a payload
/// carries both.
/// </summary>
public sealed class PaymentsDto
{
    [JsonPropertyName("items")]
    public List<PaymentDto>? RawItems { get; set; }

    [JsonPropertyName("payments")]
    public List<PaymentDto>? RawPayments { get; set; }

    [JsonPropertyName("results")]
    public List<PaymentDto>? RawResults { get; set; }

    [JsonPropertyName("orders")]
    public List<PaymentDto>? RawOrders { get; set; }

    /// <summary>Remnawave-style nesting: <c>{ response: { total, items: [...] } }</c>.</summary>
    [JsonPropertyName("response")]
    public PaymentsWrapperDto? Response { get; set; }

    /// <summary>
    /// The common REST envelope, which is either the array itself (<c>{ data: [...] }</c>) or an object
    /// wrapping it (<c>{ data: { items: [...] } }</c>). Read as a raw node so neither shape can throw
    /// the whole response away — binding it to a concrete type would make the OTHER shape a parse error.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    private List<PaymentDto>? _resolved;

    /// <summary>The history regardless of which envelope the backend used. Never null.</summary>
    [JsonIgnore]
    public List<PaymentDto> Items
    {
        get => _resolved ??= ApiJson.FirstNonEmpty(
            RawItems,
            RawPayments,
            RawResults,
            RawOrders,
            Response?.Items,
            ApiJson.ListFrom<PaymentDto>(Data, "items", "payments", "results", "orders"));
        set
        {
            RawItems = value;
            _resolved = value;
        }
    }
}

/// <summary>Nested shape: <c>{ response: { total, items: [...] } }</c>.</summary>
public sealed class PaymentsWrapperDto
{
    [JsonPropertyName("items")]
    public List<PaymentDto>? RawItems { get; set; }

    [JsonPropertyName("payments")]
    public List<PaymentDto>? RawPayments { get; set; }

    [JsonPropertyName("results")]
    public List<PaymentDto>? RawResults { get; set; }

    [JsonPropertyName("orders")]
    public List<PaymentDto>? RawOrders { get; set; }

    public int Total { get; set; }

    [JsonIgnore]
    public List<PaymentDto> Items => ApiJson.FirstNonEmpty(RawItems, RawPayments, RawResults, RawOrders);
}

/// <summary>
/// A single payment/order history entry.
///
/// THE ALTERNATES ARE NOT DECORATION. <see cref="CreatedAt"/> is load-bearing twice over: the history
/// draws the date from it AND orders itself newest-first by an ordinal compare on it, so a key that
/// fails to bind does not show up as one blank column — it also turns the sort into a no-op and leaves
/// the rows in whatever order the backend sent. <see cref="Status"/> decides whether a row is shown at
/// all. `snake_case` spellings are listed because this backend is not uniformly camelCase —
/// SubscriptionDtos already carries `tariff_id` beside `tariffId` for the same reason.
/// </summary>
public sealed class PaymentDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("paymentId")]
    public string? IdAlt1 { set => SetId(value); }

    [JsonPropertyName("payment_id")]
    public string? IdAlt2 { set => SetId(value); }

    [JsonPropertyName("uuid")]
    public string? IdAlt3 { set => SetId(value); }

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = "";

    [JsonPropertyName("order_id")]
    public string? OrderIdAlt1 { set => SetOrderId(value); }

    [JsonPropertyName("orderUuid")]
    public string? OrderIdAlt2 { set => SetOrderId(value); }

    [JsonPropertyName("number")]
    public string? OrderIdAlt3 { set => SetOrderId(value); }

    public double Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public string Provider { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("type")]
    public string? KindAlt1 { set => SetKind(value); }

    [JsonPropertyName("category")]
    public string? KindAlt2 { set => SetKind(value); }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("title")]
    public string? DescriptionAlt1 { set => SetDescription(value); }

    [JsonPropertyName("comment")]
    public string? DescriptionAlt2 { set => SetDescription(value); }

    /// <summary>ISO-8601 timestamp — feeds BOTH the displayed date and the newest-first sort.</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string? CreatedAtAlt1 { set => SetCreatedAt(value); }

    [JsonPropertyName("date")]
    public string? CreatedAtAlt2 { set => SetCreatedAt(value); }

    [JsonPropertyName("paidAt")]
    public string? CreatedAtAlt3 { set => SetCreatedAt(value); }

    [JsonPropertyName("paid_at")]
    public string? CreatedAtAlt4 { set => SetCreatedAt(value); }

    [JsonPropertyName("updatedAt")]
    public string? CreatedAtAlt5 { set => SetCreatedAt(value); }

    private void SetId(string? value)
    {
        if (Id.IsNullOrEmpty() && value.IsNotEmpty())
        {
            Id = value!;
        }
    }

    private void SetOrderId(string? value)
    {
        if (OrderId.IsNullOrEmpty() && value.IsNotEmpty())
        {
            OrderId = value!;
        }
    }

    private void SetKind(string? value)
    {
        if (Kind.IsNullOrEmpty() && value.IsNotEmpty())
        {
            Kind = value!;
        }
    }

    private void SetDescription(string? value)
    {
        if (Description.IsNullOrEmpty() && value.IsNotEmpty())
        {
            Description = value!;
        }
    }

    private void SetCreatedAt(string? value)
    {
        if (CreatedAt.IsNullOrEmpty() && value.IsNotEmpty())
        {
            CreatedAt = value!;
        }
    }
}
