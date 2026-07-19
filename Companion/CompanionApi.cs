using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Maps the JSON/stream API endpoints. All routes here are token-gated by the server's
    /// auth middleware. Filesystem access is read-only in this phase.
    /// </summary>
    internal static class CompanionApi
    {
        public static void Map(IEndpointRouteBuilder app, ICompanionHost host, CompanionEventHub hub)
        {
            app.MapGet("/api/ping", () => Results.Json(new
            {
                ok = true,
                app = "DesktopPlus Companion"
            }));

            app.MapGet("/api/panels", async () =>
            {
                var panels = await host.GetPanelsAsync().ConfigureAwait(false);
                return Results.Json(panels);
            });

            app.MapGet("/api/fs/drives", () => Results.Json(CompanionFileService.GetDrives()));

            app.MapGet("/api/panel/items", async (string? id) =>
            {
                var paths = await host.GetPanelItemsAsync(id ?? "").ConfigureAwait(false);
                if (paths == null)
                {
                    return Results.NotFound();
                }
                return Results.Json(new { id, entries = CompanionFileService.BuildEntries(paths) });
            });

            app.MapGet("/api/fs/list", (string? path) =>
            {
                var listing = CompanionFileService.ListDirectory(path);
                return listing == null ? Results.NotFound() : Results.Json(listing);
            });

            app.MapGet("/api/fs/thumb", (HttpContext context, string? path, int? size) =>
            {
                byte[]? png = CompanionThumbnails.GetPng(path, size ?? 128);
                if (png == null)
                {
                    return Results.NotFound();
                }

                // Thumbnails are safe to cache privately (own files); overrides the global no-store.
                context.Response.Headers["Cache-Control"] = "private, max-age=300";
                return Results.Bytes(png, "image/png");
            });

            // ---- mutations (Phase 2) ----
            // Bodies are parsed explicitly (JsonDocument) rather than via implicit model binding,
            // so field extraction and validation stay under our control. Ops canonicalize/validate
            // paths and never throw across this boundary.
            app.MapPost("/api/fs/mkdir", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.CreateFolder(Str(body.Value, "dir"), Str(body.Value, "name")));
            });

            app.MapPost("/api/fs/rename", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.Rename(Str(body.Value, "path"), Str(body.Value, "newName")));
            });

            app.MapPost("/api/fs/delete", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.Delete(StrArray(body.Value, "paths"), Bool(body.Value, "permanent")));
            });

            app.MapPost("/api/fs/move", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.Transfer(StrArray(body.Value, "paths"), Str(body.Value, "destDir"), move: true));
            });

            app.MapPost("/api/fs/copy", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.Transfer(StrArray(body.Value, "paths"), Str(body.Value, "destDir"), move: false));
            });

            app.MapPost("/api/open", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(CompanionOpResult.Fail("Invalid request.")); }
                return Results.Json(CompanionFileOps.OpenOnPc(Str(body.Value, "path")));
            });

            app.MapPost("/api/panel/navigate", async (HttpContext ctx) =>
            {
                var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
                if (body == null) { return Results.Json(new CompanionOpResult { Ok = false, Error = "Invalid request." }); }
                bool ok = await host.NavigatePanelAsync(Str(body.Value, "panelId") ?? "", Str(body.Value, "path") ?? "").ConfigureAwait(false);
                return Results.Json(new CompanionOpResult
                {
                    Ok = ok,
                    Error = ok ? null : "Panel not found or the folder is unavailable."
                });
            });

            app.MapGet("/api/events", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await hub.HandleConnectionAsync(socket, context.RequestAborted).ConfigureAwait(false);
            });
        }

        // ---- JSON body helpers ----
        private static async Task<JsonElement?> ReadBodyAsync(HttpContext ctx)
        {
            try
            {
                using var reader = new System.IO.StreamReader(
                    ctx.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : (JsonElement?)null;
            }
            catch
            {
                return null;
            }
        }

        private static string? Str(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static List<string> StrArray(JsonElement obj, string name)
        {
            var list = new List<string>();
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            list.Add(s);
                        }
                    }
                }
            }
            return list;
        }
    }
}
