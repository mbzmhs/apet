namespace APet.Framework.Tools.ShellIntent;

/// <summary>shell 仿真命令解析器:只解析意图,不执行。</summary>
public interface IIntentAnalyzer
{
    IntentResult Analyze(string command, string workingDirectory);
}
