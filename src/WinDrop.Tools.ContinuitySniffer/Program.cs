using System.Text.Json;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using WinDrop.Protocol.Discovery;

// Milestone 0: capture Apple's Continuity BLE advertisements on Windows.
//
// Point an iPhone's share sheet at this machine and the phone will start broadcasting
// its AirDrop beacon. We can RECEIVE that on Windows even though we can never join the
// AWDL link it is inviting us to. Confirming the payload layout here is what turns
// ADR-001 from an argument into a measurement.

bool showAll = args.Contains("--all");
bool verbose = args.Contains("--verbose");

// Bounded capture, so a run is repeatable and scriptable rather than needing Ctrl+C.
int seconds = 0;
int secondsIdx = Array.IndexOf(args, "--seconds");
if (secondsIdx >= 0 && secondsIdx + 1 < args.Length)
    int.TryParse(args[secondsIdx + 1], out seconds);

string captureDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "captures");
Directory.CreateDirectory(captureDir);
string capturePath = Path.Combine(captureDir, $"continuity-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
await using var capture = new StreamWriter(capturePath, append: true);

var seen = new Dictionary<string, int>();
int total = 0;
var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

Console.WriteLine($"Scanning for BLE advertisements ({(showAll ? "ALL" : "Apple 0x004C only")})...");
Console.WriteLine($"Capture -> {Path.GetFullPath(capturePath)}");
Console.WriteLine("On the iPhone: open any share sheet and tap AirDrop. Ctrl+C to stop.\n");

var watcher = new BluetoothLEAdvertisementWatcher
{
    // Active scanning sends SCAN_REQ so we also collect scan-response payloads.
    ScanningMode = BluetoothLEScanningMode.Active,
};

watcher.Received += (_, e) =>
{
    foreach (var mfr in e.Advertisement.ManufacturerData)
    {
        if (!showAll && mfr.CompanyId != ContinuityParser.AppleCompanyId)
            continue;

        byte[] data = ReadBuffer(mfr.Data);
        total++;

        var messages = ContinuityParser.Parse(data, out byte[] tail);

        // iOS randomises its BLE address, so this is not a stable device identity —
        // it rotates roughly every 15 minutes. Key the dedup on address+payload so a
        // rotation shows up as a new row rather than silently masking a payload change.
        string addr = FormatAddress(e.BluetoothAddress);

        foreach (var msg in messages)
        {
            string key = $"{addr}|{msg.Type:X2}|{msg.PayloadHex}";
            bool isNew = !seen.ContainsKey(key);
            seen[key] = seen.GetValueOrDefault(key) + 1;

            object record = msg.Type == (byte)ContinuityType.AirDrop
                ? Describe(ContinuityParser.DecodeAirDrop(msg.Payload))
                : new { raw = msg.PayloadHex };

            capture.WriteLine(JsonSerializer.Serialize(new
            {
                ts = e.Timestamp,
                addr,
                rssi = e.RawSignalStrengthInDBm,
                companyId = $"0x{mfr.CompanyId:X4}",
                type = $"0x{msg.Type:X2}",
                typeName = msg.TypeName,
                detail = record,
                unparsedTail = Convert.ToHexString(tail),
            }, jsonOpts));

            if (isNew || verbose)
                PrintLine(e, addr, msg, tail);
        }
    }
};

watcher.Stopped += (_, e) => Console.WriteLine($"\nWatcher stopped: {e.Error}");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };

// Pre-flight. A soft-off radio surfaces from Start() as COMException 0x800710DF
// (ERROR_DEVICE_NOT_AVAILABLE), which tells you nothing. Check first and say what to do.
try
{
    var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
    var bt = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);

    if (bt is null)
    {
        Console.Error.WriteLine("No Bluetooth radio present on this machine.");
        return 1;
    }

    if (bt.State != Windows.Devices.Radios.RadioState.On)
    {
        Console.Error.WriteLine($"Bluetooth radio is '{bt.State}'. Turn it on:");
        Console.Error.WriteLine("  Settings > Bluetooth & devices > Bluetooth  (or the Quick Settings tile)");
        Console.Error.WriteLine("Then re-run. Nothing else here works without it.");
        return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not query radio state ({ex.Message}) - attempting to scan anyway.");
}

watcher.Start();
if (seconds > 0)
{
    Console.WriteLine($"(auto-stopping after {seconds}s)");
    await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
}
else
{
    await done.Task;
}
watcher.Stop();
await capture.FlushAsync();

Console.WriteLine($"\n--- {total} Apple advertisements, {seen.Count} distinct payloads ---");
foreach (var (key, count) in seen.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {count,5}x  {key}");
Console.WriteLine($"\nSaved: {Path.GetFullPath(capturePath)}");

return 0;

static void PrintLine(BluetoothLEAdvertisementReceivedEventArgs e, string addr, ContinuityMessage msg, byte[] tail)
{
    string when = e.Timestamp.LocalDateTime.ToString("HH:mm:ss");
    string prefix = $"[{when}] {addr}  rssi={e.RawSignalStrengthInDBm,4}  {msg.TypeName,-24}";

    if (msg.Type == (byte)ContinuityType.AirDrop)
    {
        var b = ContinuityParser.DecodeAirDrop(msg.Payload);
        string flag = b.MatchedExpectedLayout ? "" : "  <-- LAYOUT MISMATCH";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{prefix} v=0x{b.Version:X2} hashes=[{b.ContactHashesHex}] raw={msg.PayloadHex}{flag}");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine($"{prefix} raw={msg.PayloadHex}");
    }

    if (tail.Length > 0)
        Console.WriteLine($"           ^ {tail.Length} unparsed trailing byte(s): {Convert.ToHexString(tail)}");
}

static object Describe(AirDropBeacon b) => new
{
    version = $"0x{b.Version:X2}",
    prefix = Convert.ToHexString(b.Prefix),
    contactHashes = b.ContactHashes.Select(h => $"{h:X4}").ToArray(),
    suffix = $"0x{b.Suffix:X2}",
    matchedExpectedLayout = b.MatchedExpectedLayout,
    raw = Convert.ToHexString(b.RawPayload),
};

static byte[] ReadBuffer(IBuffer buffer)
{
    var bytes = new byte[buffer.Length];
    DataReader.FromBuffer(buffer).ReadBytes(bytes);
    return bytes;
}

static string FormatAddress(ulong address)
{
    var b = BitConverter.GetBytes(address);
    return string.Join(":", b.Take(6).Reverse().Select(x => x.ToString("X2")));
}
