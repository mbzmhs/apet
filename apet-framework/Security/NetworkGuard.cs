using System.Net;
using System.Net.Sockets;

namespace APet.Framework.Security;

/// <summary>
/// 网络地址闸门,两处使用:
/// 1. 站点 API 的请求校验(Host/Origin 头):这两个头由浏览器协议保证不可被页面伪造——
///    恶意网页经域名重绑定打进来时 Host 是它自己的域名(拒),跨站 POST/WS 时 Origin 是
///    它自己的源(拒);地址栏直开/桌面内嵌窗口/curl 等非浏览器调用不受影响。
///    Origin 只校验源主机是否环回、不限端口:本机起的开发代理(环回主机、任意端口)照常放行。
/// 2. web_fetch 的目标校验:环回/私网/链路本地地址一律拒绝,防止提示词注入驱动的内网探测。
///    域名解析结果"任一"落在私网即拒(防解析时间差),解析失败按拒(fail-closed)。
/// </summary>
public static class NetworkGuard
{
    /// <summary>站点请求是否放行。host/origin 为原始请求头值(可空)。</summary>
    public static bool IsAllowedSiteRequest(string? host, string? origin)
    {
        // Host 头缺失(HTTP/1.0 老客户端)按拒处理:正常浏览器与 HttpClient 都会带
        if (string.IsNullOrWhiteSpace(host) || !IsLoopbackHost(host))
            return false;

        // Origin 缺省 = 非浏览器调用(脚本/进程)或无 Origin 的导航,放行;
        // 带了 Origin 就必须也是环回源(本机页面),跨站一律拒
        if (string.IsNullOrWhiteSpace(origin))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && originUri.Scheme is "http" or "https"
            && IsLoopbackHost(originUri.Host);
    }

    /// <summary>web_fetch 目标主机是否拒绝抓取(私网/环回/无法判定)。</summary>
    public static async Task<bool> IsBlockedTargetAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsPrivateIp(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            // "任一"解析结果私网即拒:域名同时配公网+内网记录时,放行等于开一道内网通道
            return addresses.Length == 0 || Array.Exists(addresses, IsPrivateIp);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return true; // 解析不出就无法判定去向,fail-closed
        }
    }

    /// <summary>主机名/字面量是否环回(127.x、localhost、::1)。host 为原始头值(可含端口/括号)。</summary>
    private static bool IsLoopbackHost(string host)
    {
        var name = host.Trim();

        if (name.StartsWith('[')) // [::1] 与 [::1]:port 两种形式
        {
            var end = name.IndexOf(']');
            return end > 0
                && IPAddress.TryParse(name[1..end], out var v6)
                && v6.Equals(IPAddress.IPv6Loopback);
        }

        if (IPAddress.TryParse(name, out var literal)) // 裸字面量(IPv4,或无端口的 IPv6)
            return literal.Equals(IPAddress.Loopback) || literal.Equals(IPAddress.IPv6Loopback);

        var colon = name.LastIndexOf(':');
        if (colon > 0 && int.TryParse(name[(colon + 1)..], out _))
            name = name[..colon]; // host:port → 取主机部分

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(name, out var ip)
                && (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback)));
    }

    /// <summary>IP 是否属于不应被外部抓取的地址段:环回/私网/链路本地/未指定/组播。</summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return ip.Equals(IPAddress.Any)
                || b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // 运营商级 NAT 段,同样非公网
                || b[0] >= 224; // 组播与保留段
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.Equals(IPAddress.IPv6Any)
                || ip.Equals(IPAddress.IPv6Loopback)
                || ip.IsIPv6LinkLocal
                || ip.IsIPv6SiteLocal
                || (b[0] & 0xFE) == 0xFC // fc00::/7 唯一本地地址
                || (b[0] & 0xF0) == 0xF0; // 组播与保留段
        }
        return true; // 其余地址族一律按不可抓取处理
    }
}
