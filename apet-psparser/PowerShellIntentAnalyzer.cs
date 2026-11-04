using System.Reflection;
using APet.Framework.Security;
using APet.Framework.Tools.ShellIntent;

namespace APet.PsParser;

/// <summary>
/// PowerShell 精确仿真解析器:运行时加载本机 PowerShell(pwsh 7 优先,回退系统
/// Windows PowerShell 5.1)的官方解析器取命令 AST,推演真实意图——命令链(; 分隔)
/// 中的每条命令、重定向(&gt; &gt;&gt; &lt;)、路径参数(-Path/-LiteralPath 与位置字符串参数)、
/// cd 的目录推演。不执行任何命令;含 $() 子表达式/命令调用/未定变量/循环结构按 Uncertain。
/// 全程反射调用,与 PowerShell 具体版本解耦(PS5/PS7 的少量命名差异用属性名回退兼容);
/// 本机未装 PowerShell 时返回 null,调用方回退保守解析器。
/// </summary>
public sealed class PowerShellIntentAnalyzer : IIntentAnalyzer
{
    private const int MaxPaths = 32;

    /// <summary>本机 PowerShell 的 S.M.A. 程序集;powershell/ps7 均未安装时 null。</summary>
    public static Assembly? LoadAutomationAssembly()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        {
            Path.Combine(pf, "PowerShell", "7", "pwsh.dll"),
            Path.Combine(pf, "PowerShell", "7", "System.Management.Automation.dll"),
            @"C:\Windows\System32\WindowsPowerShell\v1.0\System.Management.Automation.dll",
            @"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll",
            @"C:\Windows\Microsoft.NET\assembly\GAC_64\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll",
        };
        foreach (var path in candidates)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                continue;
            try
            {
                return Assembly.LoadFrom(path);
            }
            catch
            {
                // 继续尝试下一个候选
            }
        }
        // 最后回退:.NET Core 可按程序集名从 .NET Framework GAC 借用程序集
        try
        {
            return Assembly.Load(new AssemblyName("System.Management.Automation"));
        }
        catch
        {
            return null;
        }
    }

    public IntentResult Analyze(string command, string workingDirectory)
    {
        var result = new IntentResult();
        var asm = LoadAutomationAssembly();
        if (asm is null)
        {
            // 无 PowerShell 可借用:整条按 Uncertain(宿主据此询问,不误放)
            result.HasUncertain = true;
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory, Class = CommandClass.Uncertain });
            return Finish(result, workingDirectory);
        }

        object? ast = null;
        object? errors = null;
        try
        {
            var parser = asm.GetType("System.Management.Automation.Language.Parser", throwOnError: true)!;
            var ms = parser.GetMethods().Where(x => x.Name == "ParseInput" && x.IsStatic).ToList();
            var m = ms.FirstOrDefault(x => x.GetParameters().Length == 3) ?? ms.FirstOrDefault();
            if (m is not null)
            {
                if (m.GetParameters().Length == 3)
                {
                    // 签名 (string, out Token[] tokens, out ParseError[] errors):errors 在第 3 个 out 参数
                    var invokeArgs = new object?[] { command ?? "", null, null };
                    ast = m.Invoke(null, invokeArgs);
                    errors = invokeArgs[2];
                }
                else
                {
                    ast = m.Invoke(null, new object?[] { command ?? "" });
                }
            }
        }
        catch (Exception ex) when (ex is TargetException or ArgumentException or ReflectionTypeLoadException)
        {
            result.HasUncertain = true;
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory, Class = CommandClass.Uncertain });
            return Finish(result, workingDirectory);
        }

        // 官方解析器无法理解该输入 → 完全不可静态判定(errors 是 ParseError[],用 Length/Count 兼容)
        var errCount = errors switch
        {
            System.Collections.IList l => l.Count,
            var o when o.GetType().GetProperty("Count")?.GetValue(o) is int c => c,
            _ => 0
        };
        if (errCount > 0)
        {
            result.HasUncertain = true;
            result.Commands.Add(new CommandIntent { WorkingDirectoryAt = workingDirectory, Class = CommandClass.Uncertain });
            return Finish(result, workingDirectory);
        }

        if (ast is not null)
            Walk(ast, workingDirectory, result);
        else
        {
            result.HasUncertain = true;
        }
        return Finish(result, workingDirectory);
    }

    // ───────── 反射遍历 AST(属性名对 PS5/PS7 差异做回退) ─────────

    private static object? Prop(object o, params string[] names)
    {
        foreach (var n in names)
        {
            object? v = null;
            try
            {
                v = o.GetType().GetProperty(n)?.GetValue(o);
                // 部分成员是零参方法(PowerShell 属性语法可自动调用),如 FileRedirectionAst.Value()
                if (v is null)
                    v = o.GetType().GetMethod(n, Type.EmptyTypes)?.Invoke(o, null);
            }
            catch (AmbiguousMatchException)
            {
                // 同名重载(如 StringConstantExpressionAst 的 Value:object 与 Value:string):
                // 优先取非 object 具体类型的那个
                v = o.GetType().GetProperties()
                    .Where(p => p.Name == n)
                    .OrderBy(p => p.PropertyType == typeof(object) ? 1 : 0)
                    .Select(p =>
                    {
                        try { return p.GetValue(o); }
                        catch { return null; }
                    })
                    .FirstOrDefault(x => x is not null);
            }
            if (v is not null)
                return v;
        }
        return null;
    }

    private static bool IsName(object? o, string name) => o is not null && o.GetType().Name == name;

    private static IEnumerable<object> Enumerate(object? o)
    {
        if (o is IEnumerable<object> list)
            return list;
        if (o is System.Collections.IEnumerable en && o is not string)
            return en.Cast<object>();
        return [];
    }

    /// <summary>遍历 AST;返回该节点执行后的(推演)工作目录——cd 跨语句传播。</summary>
    private static string Walk(object node, string cwd, IntentResult result)
    {
        var tname = node.GetType().Name;

        switch (tname)
        {
            case "PipelineAst":
            {
                // 管道段:PS7 属性 Pipeline,PS5.1 属性 PipelineElements;管道内命令共享进入时 cwd
                var chainCwd = cwd;
                // PS7: Pipeline,PS5.1: PipelineElements
                foreach (var cmd in Enumerate(Prop(node, "Pipeline", "PipelineElements", "Commands")))
                {
                    if (!IsName(cmd, "CommandAst"))
                    {
                        // 段内嵌套结构(罕见):不可静态推演
                        result.HasUncertain = true;
                        continue;
                    }
                    var tokens = new CommandTokens();
                    CollectCommand(cmd, tokens, chainCwd, result);
                    var cls = ClassifyCommand(tokens);
                    result.Commands.Add(new CommandIntent
                    {
                        Tokens = tokens,
                        WorkingDirectoryAt = chainCwd,
                        Class = cls,
                    });
                    if (cls == CommandClass.Uncertain)
                        result.HasUncertain = true;
                    if (IsCd(tokens.Name) && tokens.Args.FirstOrDefault(a => !a.StartsWith('-')) is { } target)
                    {
                        var next = ResolvePath(chainCwd, target);
                        if (next is not null)
                            chainCwd = next;
                    }
                }
                return chainCwd;
            }

            case "CommandExpressionAst":
            {
                // 内嵌命令(& { } / 方法参数中的调用):其命令对象在 Command 属性
                // 子脚本块(& { cd x })不向外传播目录
                var inner = Prop(node, "Command");
                if (IsName(inner, "CommandAst"))
                {
                    var tokens = new CommandTokens();
                    CollectCommand(inner, tokens, cwd, result);
                    var cls = ClassifyCommand(tokens);
                    result.Commands.Add(new CommandIntent { Tokens = tokens, WorkingDirectoryAt = cwd, Class = cls });
                    if (cls == CommandClass.Uncertain)
                        result.HasUncertain = true;
                }
                else
                {
                    result.HasUncertain = true;
                }
                return cwd;
            }

            case "ScriptBlockAst":
            {
                // PS5.1 顶层脚本:语句挂在 EndBlock(NamedBlockAst);PS7 走 EndPipe
                var end = Prop(node, "EndBlock");
                if (end is not null)
                {
                    foreach (var st in Enumerate(Prop(end, "Statements")))
                        cwd = WalkStatement(st, cwd, result);
                }
                var pipe = Prop(node, "EndPipe");
                if (pipe is not null)
                    cwd = Walk(pipe, cwd, result);
                return cwd;
            }

            case "NamedBlockAst":
            {
                foreach (var st in Enumerate(Prop(node, "Statements")))
                    cwd = WalkStatement(st, cwd, result);
                return cwd;
            }

            case "IfStatementAst":
            {
                // 分支条件不可静态求值:任一分支改变了目录 → 后续目录不可推演
                var entry = cwd;
                void WalkBody(object? blk)
                {
                    if (blk is null)
                        return;
                    var c = entry;
                    foreach (var st in Enumerate(Prop(blk, "Statements")))
                        c = WalkStatement(st, c, result);
                    if (c != entry)
                        result.HasUncertain = true;
                }
                foreach (var clause in Enumerate(Prop(node, "Clauses")))
                    WalkBody(Prop(clause, "Body"));
                WalkBody(Prop(Prop(node, "ElseClause"), "Body"));
                return entry;
            }

            case "TryStatementAst":
            {
                // catch/finally 是否执行不可静态判定:任一分支改变目录 → 不可推演
                var entry = cwd;
                void WalkTryBody(object? blk)
                {
                    if (blk is null)
                        return;
                    var c = entry;
                    foreach (var st in Enumerate(Prop(blk, "Statements")))
                        c = WalkStatement(st, c, result);
                    if (c != entry)
                        result.HasUncertain = true;
                }
                WalkTryBody(Prop(node, "TryBody"));
                foreach (var cc in Enumerate(Prop(node, "CatchClauses")))
                    WalkTryBody(Prop(cc, "Body"));
                WalkTryBody(Prop(Prop(node, "FinallyClause"), "Body"));
                return entry;
            }

            default:
                // 循环/switch/其他控制结构:含任意命令文本,不可静态推演
                result.HasUncertain = true;
                return cwd;
        }
    }

    /// <summary>遍历单条语句,返回其后的工作目录。</summary>
    private static string WalkStatement(object st, string cwd, IntentResult result)
    {
        switch (st.GetType().Name)
        {
            case "PipelineAst":
            case "NamedBlockAst":
            case "IfStatementAst":
            case "TryStatementAst":
                return Walk(st, cwd, result);
            default:
                // 表达式语句中的命令调用等:保守
                if (st.GetType().Name == "ExpressionStatementAst")
                {
                    var expr = Prop(st, "Expression");
                    if (IsName(expr, "CommandExpressionAst"))
                        return Walk(expr, cwd, result);
                }
                result.HasUncertain = true;
                return cwd;
        }
    }

    // ───────── 单条 CommandAst 意图提取 ─────────

    private static void CollectCommand(object cmd, CommandTokens tokens, string cwd, IntentResult result)
    {
        // 命令名:字符串字面量才可定值;变量/表达式名 → Uncertain
        object? nameAst = null;
        try
        {
            nameAst = cmd.GetType().GetMethod("GetCommandName")?.Invoke(cmd, null);
        }
        catch
        {
            // 取命令名失败:按 Uncertain
        }
        if (nameAst is string s)
        {
            // PS5.1:GetCommandName() 直接返回字符串
            tokens.Name = s;
        }
        else if (IsName(nameAst, "StringConstantExpressionAst"))
        {
            // PS7:返回表达式 AST
            tokens.Name = (string?)Prop(nameAst, "Value");
        }
        else if (nameAst is not null)
        {
            tokens.Name = ExtentText(nameAst);
            result.HasUncertain = true;
            result.UncertainPaths.Add(tokens.Name);
        }

        var elements = Enumerate(Prop(cmd, "CommandElements", "Elements")).ToList();
        // PS5.1 中取值型非路径参数(-ErrorAction SilentlyContinue)的值是紧随的独立元素:
        // 记录"上一个参数在等值",把紧随的字符串按参数值吞掉,不当路径。
        // 仅已知取值参数吞值;开关参数(-Recurse/-Force)后是位置参数,吞掉曾致目标整体漏判
        var pendingNonPathValue = false;
        for (var i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            // 首元素是命令名本身(已单独提取):跳过
            if (i == 0 && (IsName(el, "StringConstantExpressionAst")
                           || (tokens.Name.Length == 0 && IsName(el, "VariableExpressionAst"))))
                continue;
            var ename = el.GetType().Name;

            if (pendingNonPathValue && ename == "StringConstantExpressionAst")
            {
                pendingNonPathValue = false;
                continue; // 非路径参数的值(如 SilentlyContinue),不是文件系统路径
            }
            pendingNonPathValue = false;

            // PS5.1:重定向挂在 CommandAst.Redirections(目标=Value 字符串);PS7:在元素里
            if (el is null)
                continue;

            if (ename is "FileRedirectionAst" or "MergingRedirectionAst")
            {
                var op = Prop(el, "Redirection");
                string? target = null;
                bool isInput = false;
                if (op is not null)
                {
                    var kind = Prop(op, "Kind");
                    if (kind is not null && kind.ToString() is "LessThan")
                        isInput = true;
                    // 文件目标:PS7 FileRedirectionAst.Target(表达式);PS5.1 无 Target 属性,目标紧随在元素中
                    var targetExpr = Prop(el, "Target");
                    if (targetExpr is not null)
                    {
                        if (IsName(targetExpr, "StringConstantExpressionAst"))
                            target = (string?)Prop(targetExpr, "Value");
                        else
                            result.HasUncertain = true; // 变量/表达式目标
                    }
                    if (target is null)
                    {
                        var next = FirstStringElement(cmd, el);
                        if (next is not null)
                            target = next;
                    }
                }
                if (target is not null && target.Length > 0)
                {
                    tokens.Redirects.Add(new RedirectIntent
                    {
                        Target = target,
                        Input = isInput,
                        Append = op is not null && op.GetType().GetProperty("IsAppend")?.GetValue(op) is true,
                    });
                }
                else if (op is null && !IsName(el, "MergingRedirectionAst"))
                {
                    // 拿不到重定向细节:从宽
                    result.HasUncertain = true;
                }
                continue;
            }

            if (ename is "CommandParameterAst")
            {
                var pname = (string?)Prop(el, "NameIgnoreCase", "ParameterName", "Name") ?? "";
                var lower = pname.ToLowerInvariant();
                // 参数记号回写 Args(归一为小写旗标):灾难清单与旗标扫描依赖可见的 -Recurse/-Force——
                // 此前参数整体丢弃,Remove-Item -Recurse -Force x 被判成无参命令,读写档静默放行
                if (lower.Length > 0)
                    tokens.Args.Add("-" + lower);
                var value = Prop(el, "Value");
                if (value is not null)
                {
                    // 非路径命名参数(-Filter 是通配模式…)不是文件系统路径
                    if (ShellCommandTables.PathParams.Contains(lower))
                        CollectArgument(value, tokens, result);
                }
                else if (!ShellCommandTables.PathParams.Contains(lower)
                         && ShellCommandTables.PsValueParams.Contains(lower))
                {
                    // 已知取值型非路径参数(-ErrorAction SilentlyContinue):
                    // 其值是紧随的独立元素,吞掉不当路径
                    pendingNonPathValue = true;
                }
                // 开关参数(-Recurse/-Force)与未知参数不吞紧随元素;
                // 路径类参数的分离值(-Path x 的 x)由主循环按位置参数收集,是真实路径
                continue;
            }

            if (ename is "CommandArgumentAst")
            {
                // 调用操作符 & $var / & {script}:任意命令
                var expr = Prop(el, "Expression");
                if (expr is not null && IsName(expr, "CommandExpressionAst"))
                {
                    result.HasUncertain = true;
                    continue;
                }
                CollectArgument(expr ?? el, tokens, result);
            }
            else
            {
                // 位置参数:字符串/数组/变量/子表达式
                CollectArgument(el, tokens, result);
            }
        }

        // PS5.1:重定向挂在 CommandAst.Redirections(目标=Value 字符串);PS7 无此属性
        var redirs = Enumerate(Prop(cmd, "Redirections")).ToList();
        foreach (var r in redirs)
        {
            // 目标:PS5.1 Location(表达式)/Value(),PS7 Target(表达式)
            var targetNode = Prop(r, "Location", "Target", "Value");
            string? target = targetNode switch
            {
                string str => str,
                var e when e is not null && IsName(e, "StringConstantExpressionAst") => (string?)Prop(e, "Value"),
                _ => null
            };
            if (targetNode is not null && target is null)
                result.HasUncertain = true; // 变量/表达式目标
            if (string.IsNullOrEmpty(target))
            {
                result.HasUncertain = true;
                continue;
            }
            var op = Prop(r, "Redirection");
            var kind = op is not null ? Prop(op, "Kind")?.ToString() : null;
            var fromStream = Prop(r, "FromStream")?.ToString();
            bool isInput = kind is "LessThan" or "Input"
                || (kind is null && fromStream is "Input");
            tokens.Redirects.Add(new RedirectIntent
            {
                Target = target,
                Input = isInput,
                Append = Prop(r, "Append") is true,
            });
        }
    }

    /// <summary>在命令元素中找指定元素之后的第一个字符串元素(PS5.1 重定向目标的兼容取法)。</summary>
    private static string? FirstStringElement(object cmd, object after)
    {
        var elements = Enumerate(Prop(cmd, "CommandElements", "Elements")).ToList();
        var idx = elements.FindIndex(e => ReferenceEquals(e, after));
        for (var i = idx + 1; i < elements.Count; i++)
        {
            if (IsName(elements[i], "StringConstantExpressionAst"))
                return (string?)Prop(elements[i], "Value");
            if (!IsName(elements[i], "FileRedirectionAst") && !IsName(elements[i], "MergingRedirectionAst"))
                break;
        }
        return null;
    }

    private static void CollectArgument(object expr, CommandTokens tokens, IntentResult result)
    {
        switch (expr.GetType().Name)
        {
            case "StringConstantExpressionAst":
                tokens.Args.Add((string?)Prop(expr, "Value") ?? "");
                break;
            case "ArrayLiteralAst":
            case "ParenExpressionAst":
                // 数组/括号集合 @('a','b'):只取字符串元素
                foreach (var item in Enumerate(Prop(expr, "Elements")))
                    if (IsName(item, "StringConstantExpressionAst"))
                        tokens.Args.Add((string?)Prop(item, "Value") ?? "");
                break;
            case "ConstantExpressionAst":
                // 数字/布尔常量:不是路径(-First 5 这类参数值)
                break;
            case "VariableExpressionAst":
            case "SubExpressionAst":
            case "CommandExpressionAst":
                // 变量/$( )/命令调用:展开结果不可静态定值
                result.HasUncertain = true;
                result.UncertainPaths.Add(ExtentText(expr));
                break;
            default:
                // 字符串连接/方法调用等
                result.HasUncertain = true;
                result.UncertainPaths.Add(ExtentText(expr));
                break;
        }
    }

    private static string ExtentText(object o) => Prop(o, "Extent") is { } ext && Prop(ext, "Text") is string t ? t : "";

    private static CommandClass ClassifyCommand(CommandTokens tokens)
    {
        var name = PlainName(tokens.Name);
        if (name.Length == 0)
            return CommandClass.Uncertain;
        if (IsCd(tokens.Name))
            return CommandClass.NoEffect;
        if (ShellCommandTables.NoEffect.Contains(name))
            return CommandClass.NoEffect;
        if (ShellCommandTables.Write.Contains(name))
            return CommandClass.Write;
        if (ShellCommandTables.Read.Contains(name))
            return CommandClass.Read;
        // 与 bash/cmd 一致:find 无写动作标志(-delete/-exec …)时按只读遍历处理
        if (name == "find" && ShellCommandTables.FindIsReadOnly(tokens.Args))
            return CommandClass.Read;
        return CommandClass.Uncertain;
    }

    private static bool IsCd(string name) =>
        name.Length > 0
        && (name.Equals("cd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("set-location", StringComparison.OrdinalIgnoreCase)
            || name.Equals("push-location", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pop-location", StringComparison.OrdinalIgnoreCase));

    private static string PlainName(string token)
    {
        var lower = token.ToLowerInvariant();
        foreach (var ext in new[] { ".exe", ".ps1", ".psm1", ".bat", ".cmd" })
            if (lower.EndsWith(ext))
                return token[..^ext.Length];
        return token;
    }

    // ───────── 收尾:路径收集与兜底 ─────────

    /// <summary>变量/命令引用/代码串记号:不是路径。</summary>
    private static readonly System.Text.RegularExpressions.Regex NoiseRegex =
        new(@"^(\$[A-Za-z_][A-Za-z0-9_]*|\$\{[^}]*\}|\$\([^)]*\)|`[^`]*`)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IntentResult Finish(IntentResult result, string workingDirectory)
    {
        // 重定向参与定类(与 IntentAnalyzerBase.Finish 同规则):输出重定向 = 真实写入,
        // 命令至少是 Write;目标含 $ / % 未展开记号 → 整条 Uncertain 交宿主
        foreach (var intent in result.Commands)
        {
            foreach (var r in intent.Tokens.Redirects)
            {
                if (!r.Input && intent.Class != CommandClass.Uncertain)
                    intent.Class = CommandClass.Write;
                if (r.Target.Contains('$') || r.Target.Contains('%'))
                {
                    result.HasUncertain = true;
                    result.UncertainPaths.Add(r.Target);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw, string cwd, CommandClass? cls)
        {
            if (raw is null || raw.Length == 0 || result.Paths.Count >= MaxPaths)
                return;
            if (raw.Contains('{') || raw.Contains('}'))
                return;
            if (ShellCommandTables.IsUri(raw) || raw is "/dev/null" or "/dev/zero" or "nul" or "NUL")
                return;
            // $env: 变量/含 % 记号:展开结果不可静态定值,不拼幻影路径
            if (raw.Contains('%') || raw.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
                return;
            // 含 $ 的记号($var、$env:x、变量+后缀):展开结果不可静态定值
            if (raw.Contains('$'))
                return;
            // 通配符模式(*proc、*.png 之类):是匹配模式不是具体文件
            // 前缀目录若无通配则记为该目录(检索的真实访问面)
            if (raw.Contains('*') || raw.Contains('?'))
            {
                var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
                if (cut <= 0)
                    return;
                var dirPart = raw[..cut];
                if (dirPart.Contains('*') || dirPart.Contains('?') || dirPart.Contains('$')
                    || dirPart.Contains('%') || dirPart.Contains('{') || dirPart.Contains('}')
                    || dirPart.StartsWith("/dev", StringComparison.Ordinal))
                    return;
                raw = dirPart; // 后续按普通目录路径解析
            }
            // 裸根路径:按工作目录所在盘的盘根解析(命令确实触碰整盘,丢弃会退化成 CWD 兜底)
            if (raw is "/" or "\\")
            {
                try
                {
                    var driveRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(
                        string.IsNullOrEmpty(cwd) ? "." : cwd));
                    if (driveRoot is null)
                        return;
                    raw = driveRoot;
                }
                catch
                {
                    return;
                }
            }
            // 裸盘符(G:):该盘当前目录不可静态推演,按盘根兜底会污染范围
            if (raw.Length == 2 && char.IsLetter(raw[0]) && raw[1] == ':')
                return;
            // 纯数字记号(选项值 -First 16 之类):几乎总不是文件名
            if (raw.All(char.IsDigit))
                return;
            if (cls is CommandClass.Uncertain or CommandClass.NoEffect)
            {
                if (NoiseRegex.IsMatch(raw))
                    return;
                if (raw.Contains(' ') || raw.Contains('(') || raw.Contains(')')
                    || raw.Contains(';') || raw.Length > 128)
                    return;
                if (!ShellCommandTables.LooksLikePath(raw))
                    return;
            }
            // seen(忽略大小写)保证同一路径只出现一次
            var full = ResolvePath(cwd, raw);
            if (full is not null && seen.Add(full))
                result.Paths.Add(full);
        }

        foreach (var intent in result.Commands)
        {
            var isCd = IsCd(intent.Tokens.Name);
            var skipPatternFirst = ShellCommandTables.ProgramFirstArg.Contains(PlainName(intent.Tokens.Name))
                && PlainName(intent.Tokens.Name) != "find";
            var patternSkipped = false;
            foreach (var r in intent.Tokens.Redirects)
                Add(r.Target, intent.WorkingDirectoryAt, CommandClass.Write);
            foreach (var a in intent.Tokens.Args)
            {
                if (a.StartsWith('-'))
                    continue;
                if (isCd && a.Length <= 2 && a[0] is '/' or '-')
                    continue;
                if (skipPatternFirst && !patternSkipped)
                {
                    patternSkipped = true;
                    continue;
                }
                Add(a, intent.WorkingDirectoryAt, intent.Class);
            }
        }
        // 空路径兜底用 shell 最终到达的目录(最后一条命令的工作目录)而非会话工作目录,
        // 与 IntentAnalyzerBase.Finish 一致
        var finalCwd = result.Commands.Count > 0
            ? result.Commands[^1].WorkingDirectoryAt
            : workingDirectory;

        foreach (var raw in result.UncertainPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            Add(raw, finalCwd, CommandClass.Uncertain);

        if (result.Paths.Count == 0)
            // 会话未设工作目录时 finalCwd 可能为空:退回进程目录,GetFullPath("") 会抛异常
            result.Paths.Add(System.IO.Path.GetFullPath(string.IsNullOrEmpty(finalCwd) ? "." : finalCwd));
        return result;
    }

    private static string? ResolvePath(string workingDirectory, string token)
    {
        try
        {
            var t = token; // 不展开 $env:/进程环境变量:进程环境≠PS 会话环境,记号交由噪音过滤
            // 与 PathResolver 一致:MSYS 伪设备(/dev/tty、/dev/urandom 等)不算路径
            if (t.StartsWith("/dev/", StringComparison.Ordinal) || t.StartsWith("/dev\\", StringComparison.Ordinal))
                return null;
            if (t.StartsWith('~'))
                t = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + t[1..];
            // 与 PathResolver 一致:MSYS 盘符形式 /c/...、/c:/... → C:\...
            if (t.Length >= 3 && t[0] == '/' && char.IsLetter(t[1])
                && (t[2] == '/' || t[2] == '\\' || t[2] == ':'))
            {
                t = char.ToUpperInvariant(t[1]) + ":\\" + t[2..].TrimStart('/', '\\', ':');
            }
            // 与 PathResolver 一致:Git-Bash UNC 形式 //server/share → \\server\share
            if (OperatingSystem.IsWindows() && t.StartsWith("//") && t.Length > 2)
            {
                t = "\\\\" + t[2..].Replace('/', '\\');
            }
            if (OperatingSystem.IsWindows() && t.StartsWith('/') && t != "/")
            {
                // 与 PathResolver 一致:/tmp、/var/tmp → 用户临时目录;其余 POSIX 根路径锚定虚拟工作目录所在盘
                if (t is "/tmp" or "/var/tmp"
                    || t.StartsWith("/tmp/") || t.StartsWith("/tmp\\")
                    || t.StartsWith("/var/tmp/") || t.StartsWith("/var/tmp\\"))
                {
                    var temp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp";
                    t = t.StartsWith("/var/tmp", StringComparison.Ordinal)
                        ? temp + t["/var/tmp".Length..]
                        : temp + t["/tmp".Length..];
                }
                else
                {
                    var cwdFull = System.IO.Path.GetFullPath(workingDirectory);
                    t = cwdFull[..3] + t.TrimStart('/', '\\');
                }
            }
            var full = System.IO.Path.IsPathRooted(t) ? t : System.IO.Path.Combine(workingDirectory, t);
            return System.IO.Path.GetFullPath(full);
        }
        catch
        {
            return null;
        }
    }
}
