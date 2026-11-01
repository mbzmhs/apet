namespace APet.Framework.LLM;

/// <summary>LLM 通讯过程中的错误。</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }

    public LlmException(string message, Exception innerException) : base(message, innerException) { }
}
