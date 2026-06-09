namespace ShanlianVpn.Windows.Models;

public sealed class ApiException : Exception
{
    public ApiException(string userMessage, int? statusCode = null, string? errorCode = null)
        : base(userMessage)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int? StatusCode { get; }
    public string? ErrorCode { get; }
}

