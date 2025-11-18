using System.Diagnostics;

namespace FileSyncService.Middlewares;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var ip = GetClientIP(context);
        var method = context.Request.Method;
        var path = context.Request.Path;
        var userAgent = context.Request.Headers.UserAgent.ToString();

        context.Response.OnStarting(() =>
        {
            var status = context.Response.StatusCode;
            var elapsed = sw.ElapsedMilliseconds;
            LogRequest(method, path, status, ip, userAgent, elapsed);
            return Task.CompletedTask;
        });

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var elapsed = sw.ElapsedMilliseconds;
            _logger.LogError(ex,
                "HTTP Error: [{Method}] [{Path}] from [{IP}] - {Elapsed}ms",
                method, path, ip, elapsed);
            throw;
        }
    }

    private void LogRequest(string method, string path, int statusCode, string ip, string userAgent, long elapsedMs)
    {
        var logLevel = GetLogLevel(statusCode);

        if (!_logger.IsEnabled(logLevel))
            return;

        if (logLevel >= LogLevel.Warning)
        {
            _logger.Log(
                logLevel,
                "HTTP [{Method}] [{Path}] => [{StatusCode}] from [{IP}] - {Elapsed}ms - UserAgent: {UserAgent}",
                method, path, statusCode, ip, elapsedMs, userAgent
            );
        }
        else
        {
            _logger.Log(
                logLevel,
                "HTTP [{Method}] [{Path}] => [{StatusCode}] from [{IP}] - {Elapsed}ms",
                method, path, statusCode, ip, elapsedMs
            );
        }
    }

    private static LogLevel GetLogLevel(int statusCode)
    {
        return statusCode switch
        {
            >= 500 => LogLevel.Error, // Server errors
            >= 400 => LogLevel.Warning, // Client errors
            >= 300 => LogLevel.Information, // Redirections
            _ => LogLevel.Information // Success
        };
    }

    private static string GetClientIP(HttpContext context)
    {
        const string unknownIp = "-";

        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
            return forwardedFor.FirstOrDefault()?.Split(',').First().Trim() ?? unknownIp;

        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
            return realIp.FirstOrDefault() ?? unknownIp;

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp != null && remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        return remoteIp?.ToString() ?? unknownIp;
    }
}
