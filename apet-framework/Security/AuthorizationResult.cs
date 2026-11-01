namespace APet.Framework.Security;

/// <summary>鉴权结论。</summary>
public sealed record AuthorizationResult(bool Allowed, string? Reason = null)
{
    public static AuthorizationResult Allow { get; } = new(true);

    public static AuthorizationResult Deny(string reason) => new(false, reason);
}
