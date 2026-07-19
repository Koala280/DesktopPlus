using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Owns the embedded Kestrel/HTTPS server lifecycle. Hosted in-process; Kestrel runs its
    /// own threads so request handling never blocks the WPF UI thread. Anything that touches
    /// panel/UI state must marshal to the dispatcher (done in the relevant endpoints).
    /// </summary>
    internal sealed class CompanionServer
    {
        private readonly object _gate = new object();
        private readonly CompanionRateLimiter _rateLimiter = new CompanionRateLimiter();

        private WebApplication? _app;
        private volatile string _token = string.Empty;
        private ICompanionHost? _host;
        private CompanionEventHub? _hub;

        public bool IsRunning => _app != null;
        public int Port { get; private set; }

        /// <summary>Pushes a live "panels changed" event to connected phones (no-op if stopped).</summary>
        public void NotifyPanelsChanged() => _hub?.BroadcastPanelsChanged();

        /// <summary>Starts the server. Safe to await from the UI thread; heavy work runs off-thread.</summary>
        public async Task StartAsync(int port, string token, ICompanionHost host)
        {
            if (_app != null)
            {
                return;
            }

            _token = token ?? string.Empty;
            _host = host;
            Port = port;

            var app = await Task.Run(() => BuildApp(port)).ConfigureAwait(false);
            await app.StartAsync().ConfigureAwait(false);

            lock (_gate)
            {
                _app = app;
            }
        }

        public async Task StopAsync()
        {
            WebApplication? app;
            lock (_gate)
            {
                app = _app;
                _app = null;
            }

            if (app == null)
            {
                return;
            }

            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        /// <summary>Replaces the accepted token (e.g. after regeneration); old sessions stop working.</summary>
        public void UpdateToken(string token) => _token = token ?? string.Empty;

        private WebApplication BuildApp(int port)
        {
            var cert = CompanionCertificate.LoadOrCreate();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                // Pin to Production so the developer exception page (stack traces) can never appear.
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Any, port, listenOptions =>
                {
                    listenOptions.UseHttps(cert);
                });
            });

            _hub = new CompanionEventHub();

            var app = builder.Build();
            ConfigurePipeline(app);
            return app;
        }

        private void ConfigurePipeline(WebApplication app)
        {
            // DNS-rebinding defense + security headers. A rebinding attack from a malicious
            // site arrives with a domain Host header; the phone always uses the LAN IP literal.
            app.Use(async (context, next) =>
            {
                if (!IsAllowedHost(context.Request.Host))
                {
                    context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    return;
                }

                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cache-Control"] = "no-store";
                // Least privilege: the QR scanner needs the camera on this origin only; nothing
                // here ever needs the microphone or location.
                headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
                headers["Content-Security-Policy"] =
                    "default-src 'self'; img-src 'self' data: blob:; connect-src 'self'; " +
                    "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

                await next().ConfigureAwait(false);
            });

            // Token gate for the data API. Static PWA assets are public (they hold no data;
            // the token guards every /api/* call the shell makes).
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    if (!IsAuthorized(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next().ConfigureAwait(false);
            });

            app.UseWebSockets();

            CompanionApi.Map(app, _host!, _hub!);
            CompanionWebAssets.Map(app);
        }

        private bool IsAuthorized(HttpContext context)
        {
            string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_rateLimiter.IsLockedOut(ip))
            {
                return false;
            }

            string? provided = ExtractToken(context);
            bool ok = CompanionAuth.ConstantTimeEquals(provided, _token);
            if (ok)
            {
                _rateLimiter.Reset(ip);
            }
            else
            {
                _rateLimiter.RegisterFailure(ip);
            }

            return ok;
        }

        private static string? ExtractToken(HttpContext context)
        {
            string? authorization = context.Request.Headers.Authorization;
            if (!string.IsNullOrEmpty(authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authorization.Substring("Bearer ".Length).Trim();
            }

            // Query fallback for cases where headers are awkward (WebSocket upgrade, download
            // links). Transport is HTTPS, so the query string is encrypted in transit.
            return context.Request.Query["t"];
        }

        private static bool IsAllowedHost(HostString host)
        {
            if (!host.HasValue)
            {
                return false;
            }

            if (string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Accept only IP-literal hosts (the phone connects via the LAN IP). Domain-name
            // Host headers are rejected to block DNS-rebinding attacks.
            return IPAddress.TryParse(host.Host, out _);
        }
    }
}
