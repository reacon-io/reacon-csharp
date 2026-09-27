using System.Text.Json;
using Reacon.Sdk.Streaming;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var retainedPackage = Environment.GetEnvironmentVariable("REACON_RETAINED_NUPKG");
if (retainedPackage is not null) {
    var tfm = Environment.GetEnvironmentVariable("REACON_DOTNET_TFM");
    Check(tfm is "net8.0" or "net10.0", "Unknown streaming target framework");
    Check(Environment.Version.Major == (tfm == "net8.0" ? 8 : 10), "Streaming consumer runtime major differs");
    var assembly = typeof(VerificationStreamClient).Assembly;
    using var archive = System.IO.Compression.ZipFile.OpenRead(retainedPackage);
    using var dll = archive.GetEntry($"lib/{tfm}/Reacon.Sdk.dll")!.Open();
    var loadedHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location));
    Check(System.Security.Cryptography.SHA256.HashData(dll).SequenceEqual(loadedHash), "Streaming assembly differs from retained package");
    File.WriteAllText("/results/streaming-runtime.json", JsonSerializer.Serialize(new { targetFramework = tfm,
        runtime = Environment.Version.ToString(), loadedAssemblySha256 = Convert.ToHexString(loadedHash).ToLowerInvariant(),
        assemblyMatchedRetainedPackage = true }));
}
var url = Environment.GetEnvironmentVariable("REACON_TEST_URL")!;
using var client = new VerificationStreamClient("synthetic-csharp", url);
using var isolated = new VerificationStreamClient("isolated-csharp", url);
var options = new StreamOptions { OnlyIfFree = "true" };
_ = client.StreamVerificationAsync("never@example.test");
async Task<List<VerificationEvent>> Collect(string scenario, VerificationStreamClient? owner = null, StreamOptions? settings = null)
{
    var events = new List<VerificationEvent>();
    await foreach (var item in (owner ?? client).StreamVerificationAsync(scenario + "@example.test", settings ?? options)) events.Add(item);
    return events;
}
foreach (var events in await Task.WhenAll(Collect("success"), Collect("isolated", isolated)))
{
    Check(events.Count == 4 && events[0] is StageEvent && events[1] is UnknownEvent && events[2] is ProgressEvent && events[3] is FinalEvent, "event classification");
    Check(events[0].Raw.GetProperty("label").GetString() == "hé🚀", "split UTF-8");
    Check(((FinalEvent)events[3]).Data.Result.AcceptsAll is null && ((FinalEvent)events[3]).Data.Result.Status == "future-status", "typed terminal result");
}
try { await Collect("error"); throw new Exception("Missing terminal error"); }
catch (ReaconStreamApiException error) { Check(error.Status == 200 && error.Event!.Code == "INSUFFICIENT_CREDITS" && error.Event.RemainingCredits == 0 && error.RequestId == "req-stream", "terminal error metadata"); }
foreach (var (scenario, status) in new[] { ("pre402", 402), ("pre429", 429), ("proxy", 502), ("redirect", 307) })
{
    try { await Collect(scenario); throw new Exception("Missing HTTP error"); }
    catch (ReaconStreamApiException error)
    {
        Check(error.Status == status, "HTTP status");
        if (status is 402 or 429) Check(((JsonElement)error.Body).GetProperty("code").GetString() == "FIXTURE_ERROR" && error.RequestId == "req-stream", "HTTP metadata");
        if (status == 502) Check(error.RequestId is null && error.Body is string, "proxy error");
    }
}
foreach (var scenario in new[] { "wrongtype", "malformed", "invalidresult", "eof" })
{
    try { await Collect(scenario); throw new Exception("Missing protocol error: " + scenario); } catch (ReaconProtocolException) { }
}
try { await Collect("disconnect"); throw new Exception("Missing transport error"); } catch (ReaconTransportException) { }
foreach (var phase in new[] { "idle", "total", "headers" })
{
    try { await Collect(phase, settings: options with { IdleTimeout = TimeSpan.FromMilliseconds(80), TotalTimeout = TimeSpan.FromMilliseconds(200) }); throw new Exception("Missing timeout"); }
    catch (ReaconTimeoutException error) { if (phase != "headers") Check(error.Phase == phase, "timeout phase"); }
}
using (var cancellation = new CancellationTokenSource())
{
    await using var iterator = client.StreamVerificationAsync("cancel@example.test", options, cancellation.Token).GetAsyncEnumerator();
    Check(await iterator.MoveNextAsync() && iterator.Current is StageEvent, "cancel first event");
    var pending = iterator.MoveNextAsync(); cancellation.CancelAfter(20);
    try { await pending; throw new Exception("Missing cancellation"); } catch (OperationCanceledException) { }
}
await foreach (var item in client.StreamVerificationAsync("early@example.test", options)) { Check(item is StageEvent, "early stage"); break; }
using var control = new HttpClient();
Check((await control.GetAsync(url + "/_assert_closed")).IsSuccessStatusCode, "closure while clients remain alive");
Console.WriteLine("C# streaming protocol, cancellation and live closure assertions passed");
