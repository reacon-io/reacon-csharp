// Copyright Reacon contributors. Licensed under Apache-2.0.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Reacon.Sdk.Client;
using Reacon.Sdk.Model;

namespace Reacon.Sdk.Streaming;

public sealed class ReaconProtocolException(string message) : Exception(message);
public sealed class ReaconTimeoutException(string phase) : TimeoutException($"Reacon {phase} timeout") { public string Phase { get; } = phase; }
public sealed class ReaconTransportException : IOException { public ReaconTransportException() : base("Reacon stream transport failure") {} }
public sealed class ReaconStreamApiException(int status, IReadOnlyDictionary<string, string[]> headers, object body, VerificationStreamError? streamEvent = null)
    : Exception(streamEvent is null ? $"Reacon returned HTTP {status}" : $"Reacon stream failed: {streamEvent.Code}")
{
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
    public object Body { get; } = body;
    public VerificationStreamError? Event { get; } = streamEvent;
    public string? RequestId => Headers.TryGetValue("x-request-id", out var values) ? values.FirstOrDefault() : null;
}
public abstract record VerificationEvent(JsonElement Raw);
public sealed record StageEvent(VerificationStage Data, JsonElement Payload) : VerificationEvent(Payload);
public sealed record ProgressEvent(VerificationProgress Data, JsonElement Payload) : VerificationEvent(Payload);
public sealed record FinalEvent(VerificationFinal Data, JsonElement Payload) : VerificationEvent(Payload);
public sealed record UnknownEvent(JsonElement Payload) : VerificationEvent(Payload);
public sealed record StreamOptions
{
    public string? OnlyIfFree { get; init; }
    public string? CacheMaxAge { get; init; }
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Explicit SSE client with per-instance credentials and caller-owned injected transport.</summary>
public sealed class VerificationStreamClient : IDisposable
{
    private readonly string _key;
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _owned;
    private readonly ServiceProvider _services;
    private readonly JsonSerializerOptions _json;
    public VerificationStreamClient(string apiKey, string baseUrl = "https://api.reacon.io", HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey is required", nameof(apiKey));
        _key = apiKey; _baseUrl = baseUrl.TrimEnd('/'); _owned = httpClient is null;
        // An unfinished SSE body cannot be drained for connection reuse: the server may
        // intentionally keep it open after the final event. Close it immediately.
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxResponseDrainSize = 0 }) { Timeout = Timeout.InfiniteTimeSpan };
        // Reuse every generated model converter, including date and nullable types.
        var services = new ServiceCollection();
        _ = new HostConfiguration(services);
        _services = services.BuildServiceProvider();
        _json = _services.GetRequiredService<JsonSerializerOptionsProvider>().Options;
    }
    public void Dispose() { _services.Dispose(); if (_owned) _http.Dispose(); }

    private T Decode<T>(JsonElement raw)
    {
        try { return raw.Deserialize<T>(_json) ?? throw new ReaconProtocolException("Missing verification event"); }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new ReaconProtocolException("Malformed verification event"); }
    }
    private static async Task<T> Wait<T>(Task<T> task, CancellationToken caller, CancellationToken total)
    {
        try { return await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && total.IsCancellationRequested) { throw new ReaconTimeoutException("total"); }
        catch (HttpRequestException) { throw new ReaconTransportException(); }
    }

    /// <summary>Lazy IAsyncEnumerable. Cancellation and enumerator disposal close the HTTP response; never reconnects.</summary>
    public async IAsyncEnumerable<VerificationEvent> StreamVerificationAsync(string email, StreamOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new StreamOptions();
        if (string.IsNullOrEmpty(email) || options.TotalTimeout <= TimeSpan.Zero || options.IdleTimeout <= TimeSpan.Zero) throw new ArgumentException("Email and positive timeouts are required");
        if (options.OnlyIfFree is not null and not "true" and not "false" || options.CacheMaxAge is not null and not "live" and not "1d" and not "1w" and not "1m") throw new ArgumentException("Invalid verification option");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(options.TotalTimeout);
        var url = _baseUrl + "/v1/verify?email=" + Uri.EscapeDataString(email);
        if (options.OnlyIfFree is not null) url += "&onlyIfFree=" + Uri.EscapeDataString(options.OnlyIfFree);
        if (options.CacheMaxAge is not null) url += "&cacheMaxAge=" + Uri.EscapeDataString(options.CacheMaxAge);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-API-Key", _key); request.Headers.Add("Accept", "text/event-stream");
        using var response = await Wait(_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, total.Token), cancellationToken, total.Token).ConfigureAwait(false);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        using var stream = new IdleReadStream(await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false), options.IdleTimeout, cancellationToken, total.Token);
        if (!response.IsSuccessStatusCode)
        {
            var bytes = new byte[65536]; var length = 0;
            while (length < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(length), total.Token); if (read == 0) break; length += read; }
            var text = Encoding.UTF8.GetString(bytes, 0, length); object body = text;
            try { using var document = JsonDocument.Parse(text); body = document.RootElement.Clone(); } catch (JsonException) { }
            throw new ReaconStreamApiException((int)response.StatusCode, headers, body);
        }
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new ReaconProtocolException("Expected a text/event-stream response body");
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(total.Token).ConfigureAwait(false))
        {
            JsonElement raw;
            try { using var document = JsonDocument.Parse(item.Data); raw = document.RootElement.Clone(); }
            catch (JsonException) { throw new ReaconProtocolException("Malformed SSE JSON payload"); }
            if (raw.ValueKind != JsonValueKind.Object) throw new ReaconProtocolException("Expected an SSE JSON object");
            if (raw.TryGetProperty("error", out _)) throw new ReaconStreamApiException((int)response.StatusCode, headers, raw, Decode<VerificationStreamError>(raw));
            if (raw.TryGetProperty("result", out _))
            {
                var data = Decode<VerificationFinal>(raw);
                response.Dispose(); total.CancelAfter(Timeout.InfiniteTimeSpan);
                yield return new FinalEvent(data, raw); yield break;
            }
            if (raw.TryGetProperty("stage", out _)) yield return new StageEvent(Decode<VerificationStage>(raw), raw);
            else if (raw.TryGetProperty("state", out _)) yield return new ProgressEvent(Decode<VerificationProgress>(raw), raw);
            else yield return new UnknownEvent(raw);
        }
        throw new ReaconProtocolException("Verification stream ended before a terminal event");
    }

    private sealed class IdleReadStream(Stream inner, TimeSpan idle, CancellationToken caller, CancellationToken total) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(total, cancellationToken);
            timeout.CancelAfter(idle);
            try { return await inner.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new ReaconTimeoutException(total.IsCancellationRequested ? "total" : "idle"); }
            catch (IOException) { throw new ReaconTransportException(); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
