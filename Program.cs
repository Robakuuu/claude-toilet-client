using System.Security.Claims;
using ClaudeToiletClient.Components;
using ClaudeToiletClient.Middleware;
using ClaudeToiletClient.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<PtyService>();
builder.Services.AddSingleton<HostSessionService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);
});

var app = builder.Build();

// 1. Subnet restriction (first — blocks disallowed IPs immediately)
app.UseMiddleware<SubnetRestrictionMiddleware>();

// 2. Static files (serves CSS/JS without auth)
app.UseStaticFiles();

// 3. Authentication
app.UseAuthentication();

// 4. Auth gate — redirect to /login if not authenticated
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (context.User.Identity?.IsAuthenticated != true
        && !path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/login");
        return;
    }
    await next();
});

app.UseAntiforgery();

// Login page (GET)
app.MapGet("/login", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
        return Results.Redirect("/");

    var error = ctx.Request.Query.ContainsKey("error");
    return Results.Content(GetLoginHtml(error), "text/html");
});

// Login handler (POST)
app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var password = form["password"].ToString();

    var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var expected = config.GetValue<string>("AuthPassword")
        ?? Environment.GetEnvironmentVariable("AUTH_PASSWORD")
        ?? "";

    if (!string.IsNullOrEmpty(expected) && password == expected)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "user") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return Results.Redirect("/");
    }

    return Results.Redirect("/login?error=1");
});

// Logout
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string GetLoginHtml(bool error) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8"/>
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"/>
    <title>Login — Toilet Terminal</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            background: #1a1a2e;
            color: #e2e2e2;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            display: flex;
            align-items: center;
            justify-content: center;
            height: 100vh;
            padding: 20px;
        }
        .login-box {
            background: #16213e;
            border: 1px solid #533483;
            border-radius: 8px;
            padding: 32px 28px;
            width: 100%;
            max-width: 360px;
        }
        .login-box h1 {
            font-size: 20px;
            margin-bottom: 6px;
            color: #e2e2e2;
        }
        .login-box p {
            font-size: 13px;
            color: #888;
            margin-bottom: 20px;
        }
        .login-box input[type="password"] {
            width: 100%;
            padding: 10px 12px;
            font-size: 16px;
            background: #0f3460;
            color: #e2e2e2;
            border: 1px solid #533483;
            border-radius: 4px;
            outline: none;
            margin-bottom: 14px;
        }
        .login-box input[type="password"]:focus {
            border-color: #6a42a0;
        }
        .login-box button {
            width: 100%;
            padding: 10px;
            font-size: 15px;
            background: #533483;
            color: #e2e2e2;
            border: none;
            border-radius: 4px;
            cursor: pointer;
        }
        .login-box button:hover { background: #6a42a0; }
        .error {
            background: #3d1a1a;
            border: 1px solid #8b3a3a;
            color: #e88;
            padding: 8px 12px;
            border-radius: 4px;
            font-size: 13px;
            margin-bottom: 14px;
        }
    </style>
</head>
<body>
    <div class="login-box">
        <h1>Toilet Terminal</h1>
        <p>Enter password to access the terminal</p>
        {{(error ? """<div class="error">Wrong password</div>""" : "")}}
        <form method="post" action="/login">
            <input type="password" name="password" placeholder="Password" autofocus autocomplete="current-password"/>
            <button type="submit">Login</button>
        </form>
    </div>
</body>
</html>
""";
