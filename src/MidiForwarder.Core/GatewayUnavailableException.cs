namespace MidiForwarder.Core;

public sealed class GatewayUnavailableException : IOException
{
    public GatewayUnavailableException() : base("The device is reserved for another gateway connection.") { }
    public GatewayUnavailableException(string message) : base(message) { }
    public GatewayUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
