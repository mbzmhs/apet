using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>web_fetch 的配置:超时、返回内容长度上限与代理方式。</summary>
public sealed class WebFetchToolOptions
{
    /// <summary>结果中返回的超链接条数上限。</summary>
    public const int MaxLinks = 50;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>返回给模型的正文字符数上限(超出截断,避免撑爆上下文)。</summary>
    public int MaxCharacters { get; set; } = 20_000;

    /// <summary>溢出落盘目录:正文超过视口上限时,完整内容写入这个固定缓存目录供 read_file 续读。
    /// 默认在应用数据根下,宿主配置不暴露此路径(不受目录权限档位管辖的内部例外,见 README)。
    /// 属性仅供测试重定向。</summary>
    public string SpillDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APet", "fetch-cache");

    /// <summary>落盘内容上限:单文件超过仍截断并在文件尾如实标注,防异常超大页面写爆磁盘。</summary>
    public int SpillMaxCharacters { get; set; } = 2_000_000;

    /// <summary>代理方式:system(使用系统代理)/ none(不使用)/ custom(自定义 ProxyUrl),与 LLM 连接的代理设定同源。</summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>自定义代理地址(ProxyMode=custom 时使用,如 http://127.0.0.1:7890)。</summary>
    public string ProxyUrl { get; set; } = "";
}

/// <summary>
/// 抓取网页:HTTP(S) GET 指定 URL,把响应正文返回给模型
/// (HTML 默认转 markdown 保留结构,可选纯文本/原始 HTML;JSON/文本原样返回,二进制拒绝)。
/// 浏览器 UA + Cloudflare 质询时诚实 UA 重试。
/// 效果模型:网络访问无文件系统影响(NoEffect),不触发文件鉴权——唯一例外是正文超过
/// 视口上限时把完整内容写入固定的应用缓存目录(见 WebFetchContent),该目录由宿主固定、
/// 模型不可指定,故效果模型保持 NoEffect 不变;例外在 README 安全模型中显式记载。
/// </summary>
public sealed partial class WebFetchTool : ToolBase
{
    // Chrome UA:大量站点(尤其 Cloudflare 后面)对陌生 UA 直接 403,伪装浏览器是网页抓取的事实前提;
    // cf-mitigated: challenge 的 403 会换诚实 UA 重试一次(反爬会比对 UA 与 TLS 指纹,自报家门反而更可能放行)
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";
    private const string HonestUserAgent = "APet/1.0 (web_fetch)";
    /// <summary>3xx 与页面内跳转共用的跳数上限。</summary>
    private const int MaxRedirects = 8;
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
    private const string BrowserAcceptLanguage = "zh-CN,zh;q=0.9,en;q=0.8";

    private readonly WebFetchToolOptions _options;
    private readonly ReverseMarkdown.Converter _markdownConverter = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Drop },
        Formatting = { RemoveComments = true, ListBulletChar = '-' },
    });

    public WebFetchTool(WebFetchToolOptions? options = null)
    {
        _options = options ?? new WebFetchToolOptions();
        _client = CreateClient(); // 构造即建:并发首调用不再各自建连
        // 落盘缓存过期清理(7 天):构造即扫一次,此后每次落盘顺带扫
        WebFetchContent.CleanupOldFiles(_options.SpillDirectory, TimeSpan.FromDays(7));
    }

    /// <summary>运行配置(宿主热更新代理设定时修改后需调用 ResetClient)。</summary>
    public WebFetchToolOptions Options => _options;

    // 惰性初始化在此是竞态点:两个并发工具调用同时见 null 会各建一个 HttpClient,先建者被
    // 覆盖后无人 Dispose(连接池悬挂)。构造即建 + 换旧经 Interlocked:读写都无窗口
    private HttpClient _client;

    private HttpClient Client => _client;

    private HttpClient CreateClient()
    {
        var proxyMode = _options.ProxyMode.Trim().ToLowerInvariant();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // 3xx 由 FetchAsync 手动逐跳跟进:自动跟会绕开每一跳的目标地址校验
            AllowAutoRedirect = false,
        };
        var usingProxy = false;
        switch (proxyMode)
        {
            case "none":
                handler.UseProxy = false;
                break;
            case "custom" when Uri.TryCreate(_options.ProxyUrl.Trim(), UriKind.Absolute, out var proxyUri):
                handler.Proxy = new WebProxy(proxyUri);
                usingProxy = true;
                break;
            default:
                // system:SocketsHttpHandler 默认不读系统代理(与 HttpClientHandler 不同),须显式接上
                handler.Proxy = HttpClient.DefaultProxy;
                // GetProxy 对"无代理"返回目标地址本身,以此探测系统代理是否真实存在
                var probe = new Uri("http://apet-proxy-probe.invalid/");
                usingProxy = handler.Proxy.GetProxy(probe) is { } px && px != probe;
                break;
        }
        // 无代理直连时在建连点复检目标 IP:把"预检时解析"与"实际建连时解析"之间的
        // 时间差隧道封死。代理模式连接端点是代理本身、无从校验目标,依赖逐跳预检
        if (!usingProxy)
            handler.ConnectCallback = ConnectWithPrivateAddressGuard;
        // 只用于 UA 等默认头;超时按请求用 CancellationToken 控制
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>直连建连:先按私网判据复检解析结果,再依序尝试各地址(与系统解析偏好一致)。</summary>
    private static async ValueTask<Stream> ConnectWithPrivateAddressGuard(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);

        // "任一"解析结果私网即拒:与逐跳预检同一判据,此处封预检后解析漂移的时间差
        if (addresses.Length == 0 || Array.Exists(addresses, NetworkGuard.IsPrivateIp))
            throw new HttpRequestException($"blocked target: {endPoint.Host}");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }
        throw new HttpRequestException($"connect failed: {endPoint.Host}:{endPoint.Port}", lastError);
    }

    /// <summary>代理设定变化后由宿主调用,下一个请求按新代理建连。
    /// 换旧经 Interlocked:并发请求要么拿旧实例跑完,要么拿新实例,不会拿到半初始化状态。</summary>
    public void ResetClient()
    {
        var old = System.Threading.Interlocked.Exchange(ref _client, CreateClient());
        old?.Dispose();
    }

    public override ToolDefinition Definition
    {
        get
        {
            return ToolDefinition.Create(
                "web_fetch",
                "Fetch a web page over HTTP(S) and return its content as JSON (url/status/contentType/redirects/truncated/content/links)."
                + " HTML pages are converted to markdown by default (headings, lists, code blocks and tables keep their structure);"
                + " pass format=text for plain text or format=html for raw HTML; non-HTML content (JSON etc.) is returned as-is."
                + " For HTML pages the result also contains a links array (absolute url + text, up to"
                + $" {WebFetchToolOptions.MaxLinks} entries) so you can fetch linked pages for further exploration."
                + " Intranet, loopback and link-local targets are rejected (blocked_host)."
                + $" Timeout: {_options.TimeoutSeconds} seconds. Content is truncated to {_options.MaxCharacters} characters"
                + " (pass max_chars to widen the viewport for one call, up to 100000 characters);"
                + " when truncated, the complete content is also saved to content_path (read it with read_file offset/limit),"
                + " and content_size reports the full length.",
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "The http(s) URL to fetch" },
                    "format": { "type": "string", "enum": ["markdown", "text", "html"], "description": "Output format for HTML pages; markdown (default) preserves document structure" },
                    "max_chars": { "type": "integer", "description": "Per-call inline content limit (1000-100000). Default: server-configured viewport. Overflow is still saved to content_path." }
                  },
                  "required": ["url"]
                }
                """,
                displayName: "抓取网页");
        }
    }

    /// <summary>纯网络访问,无文件系统影响,无需鉴权。例外:溢出落盘写固定应用缓存目录,
    /// 路径由 URL hash 派生、模型不可指定,故不改变效果模型(见类注释与 README)。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        string url, format;
        int maxChars;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            url = doc.RootElement.GetProperty("url").GetString() ?? "";
            format = doc.RootElement.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "markdown";
            // 单次视口放宽:模型对中等长度文档可直接申请更大上限,省掉落盘往返;
            // 钳制在 [1000, 100_000],防异常取值把上下文撑爆
            maxChars = doc.RootElement.TryGetProperty("max_chars", out var mc)
                ? WebFetchContent.ClampViewport(mc.GetInt32())
                : _options.MaxCharacters;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("max_chars must be an integer: " + ex.Message);
        }
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Url must not be empty.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Url must be an absolute http(s) URL.");
        format = format.Trim().ToLowerInvariant();
        if (format is not ("markdown" or "text" or "html"))
            throw new ArgumentException("Format must be one of: markdown (default), text, html.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            return await FetchAsync(uri, format, maxChars, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 自身超时(非整轮中断):网络问题应当作为工具结果如实回报给模型,让它重试或改道,
            // 不能抛出取消异常——那会被当作运行取消,整轮对话随之"被中断"(用户可见的中断)
            return JsonSerializer.Serialize(new
            {
                url = uri.ToString(),
                error = "timeout",
                timeoutSeconds = _options.TimeoutSeconds,
                hint = "The request did not complete in time (slow site, blocked network or unreachable host). Retry, or fetch a different URL.",
            }, ToolJson.Options);
        }
    }

    private async Task<string> FetchAsync(Uri uri, string format, int maxChars, CancellationToken timeoutToken)
    {
        // 循环抓取:HTML 里的 meta refresh / JS 跳转(如 Google 搜索的提示页)自动跟进目标地址
        string text;
        var rawHtml = "";
        var contentType = "";
        var status = 0;
        var truncated = false;
        var redirects = 0;
        var current = uri;
        var challengeRetried = false; // Cloudflare 质询的诚实 UA 重试只做一次
        var checkedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 同宿主只检一次
        while (true)
        {
            // 每跳目标过闸:环回/私网/链路本地一律拒绝(初始 URL、3xx 与页面内跳转共用此入口)
            if (checkedHosts.Add(current.Host)
                && await NetworkGuard.IsBlockedTargetAsync(current.Host, timeoutToken))
            {
                return JsonSerializer.Serialize(new
                {
                    url = current.ToString(),
                    error = "blocked_host",
                    hint = "Intranet, loopback or link-local targets are not fetchable.",
                }, ToolJson.Options);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(challengeRetried ? HonestUserAgent : ChromeUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", BrowserAccept);
            request.Headers.TryAddWithoutValidation("Accept-Language", BrowserAcceptLanguage);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
            status = (int)response.StatusCode;

            // 3xx 手动逐跳跟进:Location 相对地址对当前 URI 解析,每一跳的目标都要过上面的闸
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null || redirects >= MaxRedirects)
                    return JsonSerializer.Serialize(new
                    {
                        url = current.ToString(),
                        error = "http_error",
                        status,
                    }, ToolJson.Options);
                var target = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (target.Scheme is not ("http" or "https"))
                    return JsonSerializer.Serialize(new
                    {
                        url = current.ToString(),
                        error = "http_error",
                        status,
                    }, ToolJson.Options);
                current = target;
                redirects++;
                continue;
            }

            // Cloudflare 质询页(403 + cf-mitigated: challenge):Chrome UA 配 .NET TLS 指纹可能被识破,
            // 换诚实 UA 重试一次——反爬对自报家门的客户端更宽容(常见反爬应对)
            if (status == 403 && !challengeRetried
                && response.Headers.TryGetValues("cf-mitigated", out var mitigated)
                && mitigated.Any(v => v.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                challengeRetried = true;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new
                {
                    url = current.ToString(),
                    error = "http_error",
                    status,
                }, ToolJson.Options);

            contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!IsTextContent(contentType))
                return JsonSerializer.Serialize(new
                {
                    url = current.ToString(),
                    status,
                    contentType,
                    error = "unsupported_content_type",
                }, ToolJson.Options);

            // 限量读取正文,防止超大响应撑爆内存(上限 4MB 原始内容)
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken);
            var buffer = new byte[4096];
            var body = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, timeoutToken)) > 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length >= 4 * 1024 * 1024)
                    break;
            }

            var charset = response.Content.Headers.ContentType?.CharSet;
            text = DecodeBody(body.ToArray(), charset);
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                break;
            rawHtml = text;

            // 页面内跳转声明(meta refresh / location.replace 等)存在即跟进(与 3xx 共用跳数预算):
            // Google 等搜索引擎的"即将跳转"提示页常带页脚链接,去标签后仍较长,不能用文本长度设卡
            if (redirects >= MaxRedirects
                || !TryGetRedirectTarget(body.ToArray(), charset, response.RequestMessage?.RequestUri ?? current, out var next))
                break;
            current = next;
            redirects++;
        }

        // HTML 按请求的格式输出:markdown(默认,保留结构)/ 纯文本 / 原始 HTML;
        // 非 HTML 内容不受 format 影响,原样返回
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            text = format switch
            {
                "markdown" => HtmlToMarkdown(rawHtml, current),
                "html" => rawHtml,
                _ => HtmlToText(rawHtml),
            };

        // 视口截断 + 溢出落盘:前 N 字符照旧内联返回,完整内容落固定缓存目录供 read_file 续读——
        // "截断是门不是墙":此前超出部分直接丢弃,模型只能整页重拉(bash+curl)再提取
        truncated = text.Length > maxChars;
        string contentPath;
        long contentSize;
        var fileTruncated = false;
        if (truncated)
        {
            contentPath = WebFetchContent.BuildSpillFileName(uri, contentType, format);
            (contentPath, contentSize, fileTruncated) =
                WebFetchContent.WriteSpillFile(_options.SpillDirectory, contentPath, text, _options.SpillMaxCharacters);
            WebFetchContent.CleanupOldFiles(_options.SpillDirectory, TimeSpan.FromDays(7));
        }
        else
        {
            contentPath = "";
            contentSize = 0;
        }

        // 提取页面超链接(绝对化 + 链接文字,去重,上限 50 条)供 agent 进一步探索
        var links = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? ExtractLinks(rawHtml, current)
            : [];

        var result = new Dictionary<string, object?>
        {
            ["url"] = current.ToString(),
            ["status"] = status,
            ["contentType"] = contentType,
            ["redirects"] = redirects,
            ["truncated"] = truncated,
            ["content"] = truncated ? text[..maxChars] : text,
        };
        if (truncated)
        {
            result["content_path"] = contentPath;
            result["content_size"] = contentSize;
            result["hint"] = "Full content saved to content_path; read it with read_file (offset/limit)."
                + (fileTruncated ? " The saved file itself was truncated (exceeded the spill size limit)." : "");
        }
        result["links"] = links;
        return JsonSerializer.Serialize(result, ToolJson.Options);
    }

    /// <summary>从 HTML 提取超链接:相对地址绝对化、去 javascript:/锚点、按 URL 去重、上限 50 条。</summary>
    private static List<object> ExtractLinks(string html, Uri baseUri)
    {
        var links = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HtmlPatterns.Anchor().Matches(html))
        {
            if (links.Count >= WebFetchToolOptions.MaxLinks)
                break;
            var href = match.Groups["href"].Value.Trim();
            if (href.Length == 0 || href.StartsWith('#')
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(baseUri, href, out var resolved)
                || resolved.Scheme is not ("http" or "https")
                || !seen.Add(resolved.ToString()))
                continue;
            var text = HtmlToText(match.Groups["text"].Value).Trim();
            if (text.Length > 80)
                text = text[..80].TrimEnd() + "…";
            links.Add(new { url = resolved.ToString(), text });
        }
        return links;
    }

    private static bool IsTextContent(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || contentType.Length == 0; // 未声明类型的按文本尝试

    /// <summary>按响应声明的编码解码正文,未声明/未知编码时 UTF-8 兜底。</summary>
    private static string DecodeBody(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset).GetString(bytes);
        }
        catch (ArgumentException)
        {
            // 未知编码名,回退 UTF-8
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string HtmlToText(string html)
    {
        html = HtmlPatterns.Script().Replace(html, " ");
        html = HtmlPatterns.Style().Replace(html, " ");
        html = HtmlPatterns.Comment().Replace(html, " ");
        html = HtmlPatterns.Break().Replace(html, "\n");
        html = HtmlPatterns.BlockEnd().Replace(html, "\n");
        html = HtmlPatterns.Tag().Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        html = html.Replace('\u00A0', ' '); // &nbsp; → 普通空格,避免混入不可见的不可断行空格
        html = HtmlPatterns.Spaces().Replace(html, " ");
        html = HtmlPatterns.BlankLines().Replace(html, "\n\n");
        return html.Trim();
    }

    /// <summary>HTML → markdown(ReverseMarkdown):保留标题/列表/代码块/表格结构,LLM 消费效率远高于裸文本。
    /// 转换后把 markdown 链接的 href 统一绝对化(模型可从正文直接取到可访问地址);
    /// 先剥 script/style;极端脏页转换抛异常时退回纯文本兜底。</summary>
    private string HtmlToMarkdown(string html, Uri baseUri)
    {
        html = HtmlPatterns.Script().Replace(html, " ");
        html = HtmlPatterns.Style().Replace(html, " ");
        html = HtmlPatterns.Comment().Replace(html, " ");
        string md;
        try
        {
            md = _markdownConverter.Convert(html);
        }
        catch (Exception)
        {
            return HtmlToText(html);
        }
        md = HtmlPatterns.BlankLines().Replace(md, "\n\n"); // 转换器可能产出连续空行,压平
        md = MarkdownLink().Replace(md, m =>
        {
            var href = m.Groups["href"].Value;
            if (!Uri.TryCreate(baseUri, href, out var resolved)
                || resolved.Scheme is not ("http" or "https"))
                return m.Value; // mailto:/锚点等保持原样
            return "](" + resolved.ToString() + ")";
        });
        return md.Trim();
    }

    /// <summary>
    /// 从 HTML 提取跳转目标:meta refresh 或 JS 的 location.replace/href 赋值
    /// (Google 等搜索引擎的"即将跳转"提示页用后一种方式)。
    /// </summary>
    private static bool TryGetRedirectTarget(byte[] rawBody, string? charset, Uri baseUri, out Uri target)
    {
        var raw = DecodeBody(rawBody, charset);
        var candidates = new List<string>();

        var meta = HtmlPatterns.MetaRefresh().Match(raw);
        var js = HtmlPatterns.JsLocation().Match(raw);
        if (meta.Success)
            candidates.Add(meta.Groups["url"].Value.Trim().Trim('\''));
        if (js.Success)
            candidates.Add(js.Groups["url"].Value.Trim());

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
                continue;
            if (candidate.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Uri.TryCreate(baseUri, candidate, out var resolved) && resolved.Scheme is "http" or "https")
            {
                target = resolved;
                return true;
            }
        }
        target = null!;
        return false;
    }

    // markdown 链接 [text](href "title"):href 为不含空白/右括号的连续串,可选带一个双引号 title(替换时丢弃)
    [GeneratedRegex("\\]\\((?<href>[^)\\s]+)(?:\\s+\"[^\"]*\")?\\)")]
    private static partial Regex MarkdownLink();

    private static partial class HtmlPatterns
    {
        [GeneratedRegex("<script\\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        public static partial Regex Script();

        [GeneratedRegex("<style\\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        public static partial Regex Style();

        [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
        public static partial Regex Comment();

        [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
        public static partial Regex Break();

        [GeneratedRegex("</(p|div|li|tr|section|article|h[1-6])>", RegexOptions.IgnoreCase)]
        public static partial Regex BlockEnd();

        [GeneratedRegex("<[^>]+>")]
        public static partial Regex Tag();

        [GeneratedRegex("[ \\t\\x0B\\f\\r]+")]
        public static partial Regex Spaces();

        [GeneratedRegex("\\n\\s*\\n+")]
        public static partial Regex BlankLines();

        [GeneratedRegex("<meta[^>]*http-equiv\\s*=\\s*[\"']?refresh[\"']?[^>]*content\\s*=\\s*[\"'][^\"']*url=(?<url>[^\"'>]+)", RegexOptions.IgnoreCase)]
        public static partial Regex MetaRefresh();

        [GeneratedRegex("<a\\b[^>]*?href\\s*=\\s*[\"'](?<href>[^\"']*)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        public static partial Regex Anchor();

        // location.replace("...") / location.href="..." / window|document.location[.href]="..." / location="..."
        [GeneratedRegex("location\\.replace\\(\\s*[\"'](?<url>[^\"']+)[\"']\\s*\\)|(?:window|document)\\.location(?:\\.href)?\\s*=\\s*[\"'](?<url>[^\"']+)[\"']|[^\\.]location\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
        public static partial Regex JsLocation();
    }
}
