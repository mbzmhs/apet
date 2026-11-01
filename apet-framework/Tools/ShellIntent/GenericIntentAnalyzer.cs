namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// 保守回退解析器(PowerShell 未注入 AST 解析器时使用):通用引号/反引号分词 +
/// 重定向 + 命令词表;出现命令替换/子表达式/子命令(&amp; { }、$( )、调用操作符)一律 Uncertain。
/// 比"什么都不解析"更准,比"猜错"更保守。
/// </summary>
public sealed class GenericIntentAnalyzer : IntentAnalyzerBase
{
    public override IntentResult Analyze(string command, string workingDirectory)
    {
        var result = new IntentResult();
        var text = StripHeredocs(command ?? "");

        // 子命令/内嵌执行通道:静态解析不可靠,整条按 Uncertain 交宿主
        if (text.Contains("$(") || text.Contains("$( ") || text.Contains("`")
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"\(\s*"))
        {
            result.HasUncertain = true;
            var preTokens = Tokenize(text);
            var single = new CommandTokens();
            foreach (var t in preTokens)
            {
                if (t.Kind == TokKind.Separator)
                    continue;
                if (t.Kind == TokKind.Word && IsRedirectWord(t.Text))
                    continue;
                if (single.Name.Length == 0)
                    single.Name = t.Text;
                else
                    single.Args.Add(t.Text);
            }
            result.Commands.Add(new CommandIntent
            {
                Tokens = single,
                WorkingDirectoryAt = workingDirectory,
                Class = CommandClass.Uncertain,
            });
            return Finish(result, [new CommandSegment { Tokens = single, IsPipe = false }], workingDirectory);
        }

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory, Class = CommandClass.NoEffect });
            return Finish(result, [], workingDirectory);
        }

        var segments = new List<CommandSegment>();
        var current = new CommandTokens();
        var segRedirects = new List<RedirectIntent>();
        var currentCwd = workingDirectory;
        var pendingRedirect = false;

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

        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Separator)
            {
                EndSegment(t.Text == "|");
                continue;
            }
            if (pendingRedirect)
            {
                segRedirects.Add(new RedirectIntent { Append = t.Text.Contains(">>"), Target = t.Text });
                pendingRedirect = false;
                continue;
            }
            if (IsRedirectWord(t.Text))
            {
                pendingRedirect = true;
                continue;
            }
            if (current.Name.Length == 0)
                current.Name = t.Text;
            else
                current.Args.Add(t.Text);
        }
        EndSegment(false);

        foreach (var seg in segments)
        {
            var intent = new CommandIntent
            {
                Tokens = seg.Tokens,
                WorkingDirectoryAt = currentCwd,
            };
            result.Commands.Add(intent);
            var name = PlainCommandName(seg.Tokens.Name);
            var cls = ClassifyCommand(seg.Tokens.Name);
            if (ShellCommandTables.Cd.Contains(name))
            {
                cls = CommandClass.NoEffect;
                currentCwd = PathResolver.Resolve(currentCwd, seg.Tokens.Args.FirstOrDefault() ?? "") ?? currentCwd;
            }
            intent.Class = cls;
            if (intent.Class == CommandClass.Uncertain)
                result.HasUncertain = true;
        }

        return Finish(result, segments, workingDirectory);
    }

    /// <summary>重定向操作符词(&gt; &gt;&gt; &lt; 2&gt; 2&gt;&gt; &amp;&gt; 等);2&gt;&amp;1 流间复制无文件目标。</summary>
    private static bool IsRedirectWord(string token)
    {
        var i = 0;
        while (i < token.Length && char.IsDigit(token[i]))
            i++;
        if (i < token.Length && token[i] == '&')
            i++;
        if (i >= token.Length || (token[i] != '>' && token[i] != '<'))
            return false;
        i++;
        if (i < token.Length && token[i] == '>')
            i++;
        if (i >= token.Length)
            return true;
        if (token[i] == '&')
        {
            return !(i + 1 < token.Length && char.IsDigit(token[i + 1]));
        }
        return false;
    }

    private enum TokKind { Word, Separator }

    private sealed class Tok
    {
        public required string Text { get; init; }
        public required TokKind Kind { get; init; }
    }

    /// <summary>通用分词:双/单引号、PowerShell 反引号转义、`&gt;` `&lt;` 重定向合成、`|` `;` `&amp;&amp;` `||` 分隔。</summary>
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
            if (c == '`' && i + 1 < command.Length)
            {
                word.Append(command[++i]);
                continue;
            }
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                else
                    word.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            // 操作符续接(须先于新操作符判断):>> 追加
            if (pendingOp.Length > 0 && c == '>' && pendingOp[^1] == '>')
            {
                pendingOp.Append(c);
                continue;
            }
            if (c is '>' or '<')
            {
                var hadOp = pendingOp.Length > 0;
                if (word.Length > 0 && (word.ToString().All(char.IsDigit) || word.ToString().EndsWith('&')))
                {
                    pendingOp.Append(word.ToString());
                    word.Clear();
                }
                else
                {
                    FlushWord();
                }
                if (hadOp)
                    FlushOp();
                pendingOp.Append(c);
                continue;
            }
            if (pendingOp.Length > 0
                && (pendingOp[^1] == '>' || pendingOp[^1] == '<' || pendingOp[^1] == '&')
                && (c == '&' || char.IsDigit(c)))
            {
                pendingOp.Append(c);
                continue;
            }
            if (c == '|')
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
            if (c == '&')
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = "&&", Kind = TokKind.Separator });
                if (i + 1 < command.Length && command[i + 1] == '&')
                    i++;
                continue;
            }
            if (c == ';')
            {
                FlushOp();
                FlushWord();
                toks.Add(new Tok { Text = ";", Kind = TokKind.Separator });
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
