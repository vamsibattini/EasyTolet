namespace EasyTolet.Shared.Exceptions;

public class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(string message, int statusCode = 500, Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
