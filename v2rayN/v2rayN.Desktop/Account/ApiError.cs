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

        public Unauthorized(string? detail = null) : base("Unauthorized") => Detail = detail;
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
