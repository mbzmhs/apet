namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// bash/sh 仿真解析器:POSIX 分词(引号/反斜杠转义/变量展开)、
/// 重定向(> >> < 2> 2>&1 &amp;&gt; 与 fd 前缀)、命令链(| ; &amp;&amp; ||)与 cd 虚拟工作目录。
/// 不执行任何命令,只推演意图。命令替换 $( ) 与反引号视为不可静态解析 → 整条按 Uncertain。
/// </summary>
public sealed class BashIntentAnalyzer : IntentAnalyzerBase
{
    public override IntentResult Analyze(string command, string workingDirectory)
    {
        var result = new IntentResult();
        var tokens = Tokenize(StripHeredocs(command ?? ""));
        if (tokens.Count == 0)
        {
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory });
            return Finish(result, [], workingDirectory);
        }

        // 按 命令链分隔符 切段;管道单独成段(IsPipe)
        var segments = new List<CommandSegment>();
        var current = new CommandTokens();
        var currentCwd = workingDirectory;
        var segRedirects = new List<RedirectIntent>();
        var pendingRedirect = false; // 上一个 token 是重定向操作符,当前 token 是其文件目标
        var pendingFd = 1;
        var pendingAppend = false;

        void EndSegment(bool isPipe)
        {
            if (current.Name.Length == 0 && current.Args.Count == 0 && segRedirects.Count == 0)
            {
                segRedirects.Clear();
                return;
            }
            current.Redirects.AddRange(segRedirects);
            segments.Add(new CommandSegment { Tokens = current, IsPipe = isPipe });
            current = new CommandTokens();
            segRedirects.Clear();
        }

        foreach (var raw in tokens)
        {
            var token = raw.Text;

            if (raw.Kind == TokKind.Pipe)
            {
                EndSegment(true);
                continue;
            }
            if (raw.Kind == TokKind.Chain)
            {
                EndSegment(false);
                continue;
            }
            if (raw.Kind == TokKind.ChainSep) // && / ||(cd 的目录推演在定类阶段统一做)
            {
                EndSegment(false);
                current = new CommandTokens();
                segRedirects.Clear();
                continue;
            }

            if (pendingRedirect)
            {
                segRedirects.Add(new RedirectIntent
                {
                    Fd = pendingFd,
                    Append = pendingAppend,
                    Target = token,
                });
                pendingRedirect = false;
                continue;
            }
            if (TryParseRedirectOperator(token, out var fd, out var isInput, out var append, out bool hasFileTarget))
            {
                // 覆盖分词器独立切出的操作符(> >> <)与合并的(2> 2>&1):只要"下一个 token 是文件"即挂起
                if (hasFileTarget)
                {
                    pendingRedirect = true;
                    pendingFd = fd;
                    pendingAppend = append;
                }
                // 流间复制(2>&1):无文件目标,忽略
                continue;
            }

            if (current.Name.Length == 0)
            {
                if (IsBashOption(token))
                {
                    // 命令前的选项(罕见,如 -x cmd):无法静态判定
                    result.HasUncertain = true;
                }
                current.Name = token;
            }
            else
            {
                current.Args.Add(token);
            }
        }
        EndSegment(false);

        // 逐段定类 + cd 推演虚拟工作目录
        foreach (var seg in segments)
        {
            var intent = new CommandIntent
            {
                Tokens = seg.Tokens,
                WorkingDirectoryAt = currentCwd,
            };
            result.Commands.Add(intent);

            var name = PlainCommandName(seg.Tokens.Name);
            var cls = ClassifyCommand(seg.Tokens);
            if (ShellCommandTables.Cd.Contains(name))
            {
                // cd 改变后续命令的虚拟工作目录(只按顺序推演;|| 短路情形从宽计入)
                cls = CommandClass.NoEffect;
                var next = AdvanceCwd(currentCwd, seg.Tokens.Args.FirstOrDefault());
                if (next is not null)
                    currentCwd = next;
            }
            else if (name is "sed" && seg.Tokens.Args.Any(a => a is "-i" or "--in-place"
                     || (a.StartsWith("-i", StringComparison.Ordinal) && a.Length > 2)))
            {
                cls = CommandClass.Write;
            }
            else if (SortWritesToOutput(name, seg.Tokens.Args, windowsSwitch: false))
            {
                cls = CommandClass.Write;
            }
            else if (name.Equals("git", StringComparison.OrdinalIgnoreCase)
                     && seg.Tokens.Args.Count > 0
                     && !seg.Tokens.Args[0].StartsWith('-')
                     && ShellCommandTables.GitReadOnlySubcommands.Contains(seg.Tokens.Args[0].ToLowerInvariant()))
            {
                cls = CommandClass.Read;
            }

            // 未定变量/命令替换:展开结果不可静态判定 → 整条 Uncertain
            foreach (var a in seg.Tokens.Args)
            {
                if (a.Contains("$(") || a.Contains('`'))
                {
                    cls = CommandClass.Uncertain;
                    result.UncertainPaths.Add(a);
                    break;
                }
                for (var j = 0; j < a.Length; j++)
                {
                    if (a[j] == '$' && char.IsLetterOrDigit(a[Math.Min(j + 1, a.Length - 1)]))
                    {
                        cls = CommandClass.Uncertain;
                        break;
                    }
                }
                if (cls == CommandClass.Uncertain)
                    break;
            }

            intent.Class = cls;
            if (intent.Class == CommandClass.Uncertain)
                result.HasUncertain = true;
        }

        return Finish(result, segments, workingDirectory);
    }

    /// <summary>cd 目标解析:相对拼当前虚拟 cwd,~ 展开;空目标 = HOME;`cd -`(OLDPWD)不可推演。</summary>
    private static string? AdvanceCwd(string cwd, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (target == "-")
            return null;
        return PathResolver.Resolve(cwd, target) ?? cwd;
    }

    private static bool IsBashOption(string token) =>
        token.StartsWith('-') || token.StartsWith('/');

    // ───────── 分词(POSIX 语义) ─────────

    private enum TokKind { Word, Pipe, Chain, ChainSep }

    private sealed class Tok
    {
        public required string Text { get; init; }
        public required TokKind Kind { get; init; }
    }

    /// <summary>
    /// 是否为重定向操作符词。覆盖分词器独立切出的(&gt; &gt;&gt; &lt;)与合并的(2&gt; 2&gt;&gt; 2&gt;&amp;1);
    /// 纯数字(普通参数)返回 false。结构:fd? &amp;? (gt|lt)+ &amp;? fd?
    /// </summary>
    private static bool TryParseRedirectOperator(string token, out int fd, out bool isInput, out bool append, out bool hasFileTarget)
    {
        fd = 1; isInput = false; append = false; hasFileTarget = true;
        var i = 0;
        // fd 允许多位数(如 12>file):逐位解析,此前逐位 int.Parse 单字符互相覆盖只剩末位
        while (i < token.Length && char.IsDigit(token[i]))
            i++;
        if (i > 0)
            int.TryParse(token[..i], out fd);
        var hadFd = i > 0;
        if (i < token.Length && token[i] == '&')
            i++;
        if (i >= token.Length)
        {
            // 无 &gt;/&lt;:不是重定向(纯数字/裸 &amp; 按普通词处理)
            return false;
        }
        var op = token[i];
        if (op != '>' && op != '<')
            return false;
        isInput = op == '<';
        i++;
        if (i < token.Length && token[i] == '>')
        {
            append = true;
            i++;
        }
        if (i < token.Length)
        {
            if (token[i] == '&')
            {
                if (i + 1 < token.Length && char.IsDigit(token[i + 1]))
                {
                    // 2>&1 流间复制:无文件目标
                    hasFileTarget = false;
                }
                // "2>&" 结尾:下一 token 是文件
            }
            else
            {
                // 操作符后还有普通字符(如 >file 连写):目标并入本 token,无独立目标
                hasFileTarget = false;
            }
        }
        return true;
    }

    /// <summary>
    /// POSIX 分词:双引号内 $ ` 保留字面、单引号内全字面、反斜杠转义;
    /// &gt; &gt;&gt; &lt; 与 fd/`&amp;` 前缀合成重定向操作符 token;| ; &amp;&amp; || 为分隔符。
    /// 命令替换 $()/反引号与未定变量 $VAR 原样保留为 word(意图判定时按 Uncertain 处理)。
    /// </summary>
    private static List<Tok> Tokenize(string command)
    {
        var toks = new List<Tok>();
        var word = new System.Text.StringBuilder();
        var pendingOp = new System.Text.StringBuilder();
        char? quote = null;

        void FlushWord()
        {
            if (word.Length > 0)
            {
                toks.Add(new Tok { Text = word.ToString(), Kind = TokKind.Word });
                word.Clear();
            }
        }
        void FlushOp()
        {
            if (pendingOp.Length > 0)
            {
                toks.Add(new Tok { Text = pendingOp.ToString(), Kind = TokKind.Word });
                pendingOp.Clear();
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (quote is '"')
            {
                if (c == '"')
                {
                    quote = null;
                }
                else
                {
                    word.Append(c);
                }
                continue;
            }
            if (quote is '\'')
            {
                if (c == '\'')
                {
                    quote = null;
                }
                else
                {
                    word.Append(c);
                }
                continue;
            }

            // 反斜杠转义(行内续行 \+换行 等价于忽略两者)
            if (c == '\\' && i + 1 < command.Length)
            {
                var next = command[++i];
                if (next is '\n' or '\r')
                    continue;
                word.Append(next);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            // 操作符续接(须先于新操作符判断):>> 追加、>&/<& 流合并、2>&1 fd 复制
            if (pendingOp.Length > 0 && c is '>' or '<' or '&')
            {
                var last = pendingOp[^1];
                if ((last == '>' && c == '>')
                    || ((last == '>' || last == '<') && c == '&')
                    || (last == '&' && c is '&' or > '0' and < '9'))
                {
                    pendingOp.Append(c);
                    continue;
                }
            }

            if (c is '>' or '<')
            {
                // 合成重定向操作符:把已累积的 fd 数字 / & 前缀并入
                var hadOp = pendingOp.Length > 0;
                if (word.Length > 0)
                {
                    var w = word.ToString();
                    var isFdPrefix = w.Length > 0 && (w.All(char.IsDigit) || w.EndsWith('&'));
                    if (isFdPrefix)
                    {
                        pendingOp.Append(w);
                        word.Clear();
                    }
                    else
                    {
                        // 形如 cmd 2> 之间的普通词,先落袋
                        FlushWord();
                    }
                }
                if (hadOp)
                    FlushOp(); // 只发射并入前已存在的操作符,不能把刚并入的 fd 前缀当独立词
                pendingOp.Append(c);
                continue;
            }

            if (c == '|' && (i + 1 >= command.Length || command[i + 1] != '|'))
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = "|", Kind = TokKind.Pipe });
                continue;
            }
            if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = "||", Kind = TokKind.ChainSep });
                i++;
                continue;
            }
            if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = "&&", Kind = TokKind.ChainSep });
                i++;
                continue;
            }
            if (c == '&' && i + 1 < command.Length && command[i + 1] == '>')
            {
                // bash &>(与 >& 等价):stdout+stderr 合并重定向到文件,不是裸 &(后台)——
                // 此前被当裸 & 切断,后续 "> file" 落进无名段按 Uncertain 定类,每次误弹鉴权卡
                FlushWord();
                pendingOp.Append('&');
                pendingOp.Append('>');
                i++;
                continue;
            }
            if (c == ';' || (c == '&' && !(pendingOp.Length > 0 && (pendingOp[^1] == '>' || pendingOp[^1] == '<'))))
            {
                FlushOp();
                FlushWord();
                toks.Add(c == ';'
                    ? new Tok { Text = ";", Kind = TokKind.Chain }
                    // 裸 &(后台任务):同 ; 语义(顺序上独立)——必须切段,否则后续命令
                    // 全部并入前一条(echo a & rm -rf x 曾被整段按 echo 定类放行)
                    : new Tok { Text = "&", Kind = TokKind.Chain });
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushOp();
                FlushWord();
                continue;
            }

            FlushOp();
            word.Append(c);
        }
        FlushOp();
        FlushWord();
        return toks;
    }
}
