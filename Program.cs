// Program.cs
using System.Text.Json.Nodes;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using CallAutomation.AzureAI.VoiceLive;

var builder = WebApplication.CreateBuilder(args);

// Registry to bridge AnswerCall → /ws
builder.Services.AddSingleton<ICallConnectionRegistry, CallConnectionRegistry>();
// TTS + STT services
builder.Services.AddSingleton<SpeechTtsService>();
builder.Services.AddSingleton<SpeechSttService>();

// ACS Connection String (loaded from configuration, not hardcoded)
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

// Optional: WS signing key (loaded from configuration, not hardcoded)
var wsSigningKey = builder.Configuration.GetValue<string>("WsSigningKey");

var client = new CallAutomationClient(acsConnectionString);

var app = builder.Build();

// Base URL resolve (use configuration or environment variables)
var appBaseUrl = builder.Configuration["BaseUri"]?.TrimEnd('/');
if (string.IsNullOrEmpty(appBaseUrl))
{
    var tunnel = Environment.GetEnvironmentVariable("TUNNEL_URL")?.TrimEnd('/');
    var host = Environment.GetEnvironmentVariable("HOSTNAME");
    appBaseUrl = !string.IsNullOrEmpty(tunnel) ? tunnel :
                 !string.IsNullOrEmpty(host) ? $"https://{host}" : null;
}

app.MapGet("/", () => "Hello ACS CallAutomation!");
app.MapGet("/healthz", () => Results.Ok("OK"));

// IncomingCall handler
app.MapPost("/api/incomingCall", async (
    HttpContext http,
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger,
    ICallConnectionRegistry registry) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // 1) Subscription validation
        if (eventGridEvent.TryGetSystemEventData(out var systemData) &&
            systemData is SubscriptionValidationEventData subVal)
        {
            var responseData = new SubscriptionValidationResponse { ValidationResponse = subVal.ValidationCode };
            return Results.Ok(responseData);
        }

        // 2) Only handle IncomingCall here
        if (!string.Equals(eventGridEvent.EventType, "Microsoft.Communication.IncomingCall", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Ignoring non-IncomingCall event type: {type}", eventGridEvent.EventType);
            continue;
        }

        var jsonObject = JsonNode.Parse(eventGridEvent.Data!.ToString())!.AsObject();
        var callerId = jsonObject["callerId"]?.GetValue<string>() ?? "unknown";
        var incomingCallContext = jsonObject["incomingCallContext"]?.GetValue<string>() ?? "";
        var serverCallId = jsonObject["serverCallId"]?.GetValue<string>() ?? "";

        var correlationId = Guid.NewGuid().ToString("N");
        var callbackUri = new Uri(new Uri(appBaseUrl!), $"/api/callbacks/{Guid.NewGuid()}?callerId={Uri.EscapeDataString(callerId)}");

        static string ToWebSocketBase(string baseUrl)
        {
            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + baseUrl.Substring("https://".Length);
            if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + baseUrl.Substring("http://".Length);
            return baseUrl.StartsWith("ws", StringComparison.OrdinalIgnoreCase) ? baseUrl : "wss://" + baseUrl.TrimStart('/');
        }

        var baseWssOverride = builder.Configuration["BaseWssUri"];
        var wssBase = !string.IsNullOrWhiteSpace(baseWssOverride)
            ? (baseWssOverride.Contains("://", StringComparison.Ordinal) ? baseWssOverride : $"wss://{baseWssOverride}")
            : ToWebSocketBase(appBaseUrl!);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string? sig = null;
        if (!string.IsNullOrWhiteSpace(wsSigningKey))
        {
            sig = Helpers.ComputeHmac($"{correlationId}\n{ts}", wsSigningKey!);
        }

        var wsUriBuilder = new StringBuilder($"{wssBase}/ws?corrId={Uri.EscapeDataString(correlationId)}&callerId={Uri.EscapeDataString(callerId)}&ts={Uri.EscapeDataString(ts)}");
        if (!string.IsNullOrEmpty(sig)) wsUriBuilder.Append($"&sig={Uri.EscapeDataString(sig)}");
        var websocketUri = wsUriBuilder.ToString();

        var mediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
        {
            TransportUri = new Uri(websocketUri),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm16KMono
        };

        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            MediaStreamingOptions = mediaStreamingOptions,
        };

        try
        {
            var answerResponse = await client.AnswerCallAsync(options);
            var answerCallResult = answerResponse.Value;
            logger.LogInformation("Answered call. CallConnectionId: {ccid}; ServerCallId: {scid}",
                answerCallResult.CallConnection.CallConnectionId, serverCallId);

            registry.Set(correlationId, answerCallResult.CallConnection);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.ErrorCode == "8523")
        {
            logger.LogWarning("AnswerCall skipped: {code} - {msg}", ex.ErrorCode, ex.Message);
        }
    }

    return Results.Ok();
});

// Call Automation callbacks
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation("Event received: {evt}", JsonConvert.SerializeObject(@event, Formatting.Indented));
    }
    return Results.Ok();
});

// WebSocket keep-alive
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// /ws endpoint
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                var corrId = context.Request.Query["corrId"].ToString();
                var ts = context.Request.Query["ts"].ToString();
                var sig = context.Request.Query["sig"].ToString();
                var callerId = context.Request.Query["callerId"].ToString();

                if (string.IsNullOrWhiteSpace(corrId) || string.IsNullOrWhiteSpace(ts))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Missing corrId or ts.");
                    return;
                }

                var wsSigningKeyLocal = builder.Configuration.GetValue<string>("WsSigningKey");
                if (!string.IsNullOrWhiteSpace(wsSigningKeyLocal))
                {
                    if (string.IsNullOrWhiteSpace(sig))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Missing signature.");
                        return;
                    }
                    var expected = Helpers.ComputeHmac($"{corrId}\n{ts}", wsSigningKeyLocal!);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (!Helpers.FixedTimeEquals(sig, expected) || (now - long.Parse(ts) > 60))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Invalid or expired signature.");
                        return;
                    }
                }

                var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                var registry = context.RequestServices.GetRequiredService<ICallConnectionRegistry>();
                var tts = context.RequestServices.GetRequiredService<SpeechTtsService>();
                var stt = context.RequestServices.GetRequiredService<SpeechSttService>();

                var mediaService = new AcsMediaStreamingHandler(webSocket, builder.Configuration, tts, stt, corrId, callerId);

                // Map corrId → CallConnection in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        CallConnection? callConn = null;
                        const int maxWaitMs = 30000;
                        const int stepMs = 100;
                        var waited = 0;
                        while (waited <= maxWaitMs && !registry.TryGet(corrId, out callConn!))
                        {
                            await Task.Delay(stepMs);
                            waited += stepMs;
                        }
                        if (callConn != null)
                        {
                            mediaService.SetCallConnection(callConn);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("WebSocket mapping task error: {ex}", ex);
                    }
                });

                await mediaService.ProcessWebSocketAsync(context.RequestAborted);
                registry.Remove(corrId);
            }
            catch (Exception ex)
            {
                logger.LogError("WebSocket exception: {ex}", ex);
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

app.Run();