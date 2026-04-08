namespace Flattiverse.Gateway.Application.Exceptions;

public sealed class GatewayClientMessageException : Exception
{
    public GatewayClientMessageException(string code, string message, bool recoverable)
        : base(message)
    {
        Code = code;
        Recoverable = recoverable;
    }

    public string Code { get; }

    public bool Recoverable { get; }
}