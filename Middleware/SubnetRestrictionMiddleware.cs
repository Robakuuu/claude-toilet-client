using System.Net;

namespace ClaudeToiletClient.Middleware;

public class SubnetRestrictionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly List<IPNetwork> _allowedNetworks;
    private readonly ILogger<SubnetRestrictionMiddleware> _logger;

    public SubnetRestrictionMiddleware(
        RequestDelegate next,
        IConfiguration config,
        ILogger<SubnetRestrictionMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var subnets = config.GetValue<string>("AllowedSubnets")
            ?? Environment.GetEnvironmentVariable("ALLOWED_SUBNETS")
            ?? "";

        _allowedNetworks = subnets
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => IPNetwork.Parse(s))
            .ToList();

        if (_allowedNetworks.Count > 0)
            _logger.LogInformation("Subnet restriction enabled: {Subnets}", subnets);
        else
            _logger.LogWarning("No ALLOWED_SUBNETS configured — all IPs allowed (password-only protection)");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowedNetworks.Count == 0)
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp == null)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        if (IPAddress.IsLoopback(remoteIp))
        {
            await _next(context);
            return;
        }

        if (_allowedNetworks.Any(n => n.Contains(remoteIp)))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Blocked request from {IP}", remoteIp);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Forbidden");
    }
}
