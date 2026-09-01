namespace v2rayN.Desktop.Account;

/// <summary>
/// Sealed hierarchy of failures the API layer can surface. Callers pattern-match on these and never
/// see raw exceptions. Messages must never contain tokens or subscription URLs. Ported 1:1 from
/// V2rayNG auth/ApiError.kt.
/// </summary>
public abstract class ApiError : Exception
{
    protected ApiError(string message, Exception? cause = null) : base(message, cause)
    {
    }

    /// <summary>Backend not configured (blank base URL) — login should not be offered.</summary>
    public sealed class NotConfiguredError : ApiError
    {
        public NotConfiguredError() : base("Backend not configured")
        {
        }
    }

    /// <summary>Network/IO failure (no connectivity, DNS, TLS).</summary>
    public sealed class NetworkError : ApiError
    {
        public NetworkError(Exception? cause = null) : base("Network error", cause)
        {
        }
    }

    /// <summary>A request (or the login flow) exceeded its time budget.</summary>
    public sealed class TimeoutError : ApiError
    {
        public TimeoutError() : base("Request timed out")
        {
        }
    }

    /// <summary>
    /// 401 — session invalid or expired. Detail carries a sanitized snippet of the response body
    /// (payment diagnostics), never a token/URL.
    /// </summary>
    public sealed class Unauthorized : ApiError
    {
        public string? Detail { get; }

        /// <summary>
        /// True when the 401 actually came from the departament backend, false when it came from
        /// something else standing in the way.
        ///
        /// A 401 is the ONE answer allowed to end a session, so it matters who said it. A captive
        /// portal — hotel, airport, office wifi, the exact networks a VPN user meets — answers every
        /// request with 401 and an HTML login page, and the app read that as «твой токен мёртв» and
        /// signed the user out of an account whose token was perfectly fine. Provenance is judged by
        /// the response's content type: our API answers JSON (or nothing), a portal answers a page.
        /// Defaults to true so a call site that cannot judge behaves exactly as before.
        /// </summary>
        public bool FromApi { get; }

        public Unauthorized(string? detail = null, bool fromApi = true) : base("Unauthorized")
        {
            Detail = detail;
            FromApi = fromApi;
        }
    }

    /// <summary>404 — resource not found (also the "keep polling" signal for telegram-login-check).</summary>
    public sealed class NotFoundError : ApiError
    {
        public NotFoundError() : base("Not found")
        {
        }
    }

    /// <summary>410 — resource gone / login token expired.</summary>
    public sealed class GoneError : ApiError
    {
        public GoneError() : base("Gone")
        {
        }
    }

    /// <summary>429 — too many requests.</summary>
    public sealed class RateLimited : ApiError
    {
        public RateLimited() : base("Rate limited")
        {
        }
    }

    /// <summary>502/503 — backend temporarily unavailable.</summary>
    public sealed class ServiceUnavailable : ApiError
    {
        public ServiceUnavailable() : base("Service unavailable")
        {
        }
    }

    /// <summary>
    /// Any other unexpected non-2xx status. Detail carries a sanitized snippet of the response body
    /// (payment diagnostics), never a token/URL.
    /// </summary>
    public sealed class Server : ApiError
    {
        public int Code { get; }
        public string? Detail { get; }

        public Server(int code, string? detail = null) : base($"Server error ({code})")
        {
            Code = code;
            Detail = detail;
        }
    }

    /// <summary>Response body could not be parsed into the expected shape.</summary>
    public sealed class Parse : ApiError
    {
        public Parse(Exception? cause = null) : base("Failed to parse response", cause)
        {
        }
    }
}
