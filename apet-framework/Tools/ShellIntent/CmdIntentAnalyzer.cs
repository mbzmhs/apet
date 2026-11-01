namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// Windows CMD 仿真解析器:cmd 语法(`^` 转义、`%VAR%` 变量、`"..."` 引号、
/// `&amp;` `&amp;&amp;` `||` `^&amp;` 命令链、`&gt;` `&gt;&gt;` `&lt;` 重定向、`(` `)` 代码块、
/// `:label` 批处理标签)。不执行,只推演意图;for/if 等控制结构含任意命令文本 → Uncertain。
/// </summary>
public sealed class CmdIntentAnalyzer : IntentAnalyzerBase
{
    public override IntentResult Analyze(string command, string workingDirectory)
    {
        var result = new IntentResult();
        var tokens = Tokenize(command ?? "");
        if (tokens.Count == 0)
        {
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory });
            return Finish(result, [], workingDirectory);
        }

        var segments = new List<CommandSegment>();
        var current = new CommandTokens();
        var segRedirects = new List<RedirectIntent>();
        var currentCwd = workingDirectory;
        var sawLabel = false;
        var pendingRedirect = false;
        var pendingFd = 1;
        var pendingAppend = false;

        void EndSegment()
        {
            if (current.Name.Length == 0 && current.Args.Count == 0 && segRedirects.Count == 0)
            {
                segRedirects.Clear();
                return;
            }
            current.Redirects.AddRange(segRedirects);
            segments.Add(new CommandSegment { Tokens = current, IsPipe = false });
            current = new CommandTokens();
            segRedirects.Clear();
        }

        void MarkUncertain(string? hint)
        {
            result.HasUncertain = true;
            if (hint is not null)
                result.UncertainPaths.Add(hint);
        }

        foreach (var raw in tokens)
        {
            var token = raw.Text;

            if (raw.Kind == TokKind.Separator)
            {
                EndSegment();
                if (token is "(" or ")")
                    MarkUncertain(null); // 代码块/条件块:内含任意命令文本
                continue;
            }

            if (pendingRedirect)
            {
                segRedirects.Add(new RedirectIntent { Fd = pendingFd, Append = pendingAppend, Target = token });
                pendingRedirect = false;
                continue;
            }
            if (TryParseRedirectOperator(token, out var fd, out var append, out var hasFileTarget))
            {
                if (hasFileTarget)
                {
                    pendingRedirect = true;
                    pendingFd = fd;
                    pendingAppend = append;
                }
                continue;
            }

            if (token.StartsWith(':'))
            {
                // 批处理标签(:goto 目标)落入一次性命令:其后逻辑不可静态推演
                sawLabel = true;
                MarkUncertain(null);
                continue;
            }
            if (token.StartsWith('%'))
            {
                // %VAR% 未展开/%% 循环变量:静态不可定值
                MarkUncertain(token);
                if (current.Name.Length == 0)
                    current.Name = token;
                else
                    current.Args.Add(token);
                continue;
            }

            if (current.Name.Length == 0)
            {
                current.Name = token;
                var plain = PlainCommandName(token);
                if (plain is "for" or "call" or "start" or "if"
                    || plain is "pushd" or "popd")
                {
                    // for 循环体/call 脚本/start 任意程序/if 分支:内含任意命令,不可静态推演
                    MarkUncertain(null);
                }
            }
            else
            {
                current.Args.Add(token);
            }
        }
        EndSegment();

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
                // cmd 的 cd 可带 /d|-d|/s 前缀(同时切盘);cd - 回上一目录不可推演
                cls = CommandClass.NoEffect;
                var args = seg.Tokens.Args;
                string? target = args.FirstOrDefault();
                if (target is not null && target.ToLowerInvariant() is "/d" or "-d" or "/s")
                    target = args.ElementAtOrDefault(1);
                else if (target == "-")
                    target = null;
                if (target is not null && target.Length > 0)
                {
                    var next = PathResolver.Resolve(currentCwd, target);
                    if (next is not null)
                        currentCwd = next;
                }
            }
            else if (SortWritesToOutput(name, seg.Tokens.Args, windowsSwitch: true))
            {
                // sort /O 输出文件是写目标:不能留在 Read 级免检
                cls = CommandClass.Write;
            }
            intent.Class = cls;
            if (intent.Class == CommandClass.Uncertain)
                result.HasUncertain = true;
        }
        if (sawLabel)
            result.HasUncertain = true;

        return Finish(result, segments, workingDirectory);
    }

    /// <summary>cmd 的 Windows 风格开关(/v /d /mir):不是路径。带 / 的 MSYS 路径(/c/...、/etc)
    /// 含分隔符不在此列;/c(盘符形式)在 cmd 语境下只会是 cmd.exe 的开关。</summary>
    protected override bool IsOptionLike(string arg) =>
        arg.StartsWith('-')
        || (arg.Length >= 2 && arg.Length <= 8 && arg[0] == '/'
            && arg[1..].All(char.IsLetterOrDigit));

    /// <summary>是否为重定向操作符词(cmd: &gt; &gt;&gt; &lt; 与 fd 前缀);纯数字是普通参数。</summary>
    private static bool TryParseRedirectOperator(string token, out int fd, out bool append, out bool hasFileTarget)
    {
        fd = 1;
        append = false;
        hasFileTarget = true;
        var i = 0;
        while (i < token.Length && char.IsDigit(token[i]))
        {
            fd = int.Parse(token[i..(i + 1)]);
            i++;
        }
        if (i >= token.Length)
            return false; // 纯数字是普通参数
        var op = token[i];
        if (op != '>' && op != '<')
            return false;
        i++;
        if (i < token.Length && token[i] == '>')
        {
            append = true;
            i++;
        }
        if (i < token.Length)
        {
            // >file 连写:目标并入本 token,无独立目标
            hasFileTarget = false;
        }
        return true;
    }

    private enum TokKind { Word, Separator }

    private sealed class Tok
    {
        public required string Text { get; init; }
        public required TokKind Kind { get; init; }
    }

    /// <summary>
    /// cmd 分词:`^` 转义下一字符(含引号内)、`"..."` 引号字面(引号保留在 token 中)、
    /// `&gt;` `&gt;&gt;` `&lt;` 与 fd 前缀合成重定向操作符;`&amp;` `&amp;&amp;` `||` `^&amp;` 与 `(` `)` 为分隔符。
    /// </summary>
    private static List<Tok> Tokenize(string command)
    {
        var toks = new List<Tok>();
        var word = new System.Text.StringBuilder();
        var pendingOp = new System.Text.StringBuilder();
        var inQuote = false;

        void FlushWord()
        {
            if (word.Length > 0)
            {
                toks.Add(new Tok { Text = Unquote(word.ToString()), Kind = TokKind.Word });
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

            // ^ 转义:下一字符按字面处理
            if (c == '^' && i + 1 < command.Length)
            {
                var next = command[++i];
                if (next == '^' && i + 1 < command.Length)
                {
                    // 转义链(如 ^&^&):跳过连续转义符,取最终被转义的字符
                    i++;
                    next = command[i];
                }
                if (inQuote || next is not ('&' or '>' or '<' or '(' or ')' or '"' or ' ' or '|'))
                {
                    word.Append(next);
                }
                else
                {
                    // 转义后的分隔符:当作分隔处理
                    if (next == '&')
                    {
                        FlushOp();
                        FlushWord();
                        if (i + 1 < command.Length && command[i + 1] == '&')
                        {
                            toks.Add(new Tok { Text = "&&", Kind = TokKind.Separator });
                            i++;
                        }
                        else
                        {
                            toks.Add(new Tok { Text = "&", Kind = TokKind.Separator });
                        }
                    }
                    else if (next == '|')
                    {
                        FlushOp();
                        FlushWord();
                        toks.Add(new Tok { Text = "|", Kind = TokKind.Separator });
                        if (i + 1 < command.Length && command[i + 1] == '|')
                        {
                            toks.Add(new Tok { Text = "||", Kind = TokKind.Separator });
                            i++;
                        }
                    }
                    else if (next == '>' || next == '<')
                    {
                        FlushOp();
                        pendingOp.Append(next);
                    }
                    else if (next == ';')
                    {
                        FlushOp();
                        FlushWord();
                        toks.Add(new Tok { Text = ";", Kind = TokKind.Separator });
                    }
                    else
                    {
                        word.Append(next);
                    }
                }
                continue;
            }

            if (c == '"')
            {
                inQuote = !inQuote;
                word.Append(c); // 引号保留:cmd 的引号是 token 边界标记也是字面,保留可暴露带引号路径
                continue;
            }

            // 操作符续接(须先于新操作符判断):>> 追加
            if (!inQuote && pendingOp.Length > 0 && c == '>' && pendingOp[^1] == '>')
            {
                pendingOp.Append(c);
                continue;
            }

            if (!inQuote && (c == '>' || c == '<'))
            {
                var hadOp = pendingOp.Length > 0;
                if (word.Length > 0)
                {
                    var w = word.ToString();
                    if (w.All(char.IsDigit) || w.EndsWith('&'))
                    {
                        pendingOp.Append(Unquote(w));
                        word.Clear();
                    }
                    else
                    {
                        FlushWord();
                    }
                }
                if (hadOp)
                    FlushOp();
                pendingOp.Append(c);
                continue;
            }

            if (pendingOp.Length > 0
                && (pendingOp[^1] == '>' || pendingOp[^1] == '<')
                && (c == '&' || char.IsDigit(c)))
            {
                pendingOp.Append(c);
                continue;
            }

            if (!inQuote && c == '&')
            {
                FlushOp();
                FlushWord();
                if (i + 1 < command.Length && command[i + 1] == '&')
                {
                    toks.Add(new Tok { Text = "&&", Kind = TokKind.Separator });
                    i++;
                }
                else
                {
                    toks.Add(new Tok { Text = "&", Kind = TokKind.Separator });
                }
                continue;
            }

            if (!inQuote && c == '|')
            {
                FlushOp();
                FlushWord();
                if (i + 1 < command.Length && command[i + 1] == '|')
                {
                    toks.Add(new Tok { Text = "||", Kind = TokKind.Separator });
                    i++;
                }
                else
                {
                    toks.Add(new Tok { Text = "|", Kind = TokKind.Separator });
                }
                continue;
            }

            if (!inQuote && (c == '(' || c == ')'))
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = c.ToString(), Kind = TokKind.Separator });
                continue;
            }

            // ; 在 cmd 中不是分隔符(是普通字符),但模型生成的命令习惯用 ; 拼接:
            // 按分隔符从宽处理(多分析不漏分析)
            if (!inQuote && c == ';')
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = ";", Kind = TokKind.Separator });
                continue;
            }

            if (!inQuote && char.IsWhiteSpace(c))
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

    /// <summary>剥掉成对双引号(cmd 引号仅作边界标记)。</summary>
    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
            return text[1..^1];
        return text;
    }
}
