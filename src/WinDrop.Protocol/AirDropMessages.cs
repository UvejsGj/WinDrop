namespace WinDrop.Protocol;

/// <summary>
/// One file as described in the /Ask body.
///
/// <see cref="FileBomPath"/> is the link between the two halves of the protocol: it is
/// the name the same file will carry inside the cpio archive sent to /Upload. The
/// receiver uses it to tie an archive member back to the metadata the user actually saw
/// and consented to, so the two must agree exactly.
/// </summary>
public sealed record AirDropFileEntry(
    string FileName,
    string FileType,
    string FileBomPath,
    bool IsDirectory = false,
    bool ConvertMediaFormats = false)
{
    /// <summary>Uniform Type Identifier for content we cannot classify.</summary>
    public const string DefaultFileType = "public.data";

    public static AirDropFileEntry ForFile(string fileName, string? fileType = null) =>
        new(fileName, fileType ?? DefaultFileType, $"./{fileName}");

    public Dictionary<string, object?> ToPlist() => new()
    {
        ["FileName"] = FileName,
        ["FileType"] = FileType,
        ["FileBomPath"] = FileBomPath,
        ["FileIsDirectory"] = IsDirectory,
        // Apple writes this as an integer here, not a boolean, even though the
        // top-level field of the same name is a boolean.
        ["ConvertMediaFormats"] = ConvertMediaFormats ? 1L : 0L,
    };

    public static AirDropFileEntry FromPlist(IReadOnlyDictionary<string, object?> plist) => new(
        FileName: plist.GetValueOrDefault("FileName") as string ?? "",
        FileType: plist.GetValueOrDefault("FileType") as string ?? DefaultFileType,
        FileBomPath: plist.GetValueOrDefault("FileBomPath") as string ?? "",
        IsDirectory: plist.GetValueOrDefault("FileIsDirectory") is true,
        ConvertMediaFormats: plist.GetValueOrDefault("ConvertMediaFormats") is 1L or true);
}

/// <summary>
/// The /Ask body. This is what the receiving user is shown before deciding, so every
/// field in it is attacker-controlled text that will be put in front of a human — the
/// receiver must treat it as display data, never as a filesystem path.
/// </summary>
public sealed record AirDropAskRequest(
    string SenderComputerName,
    string SenderModelName,
    string SenderId,
    string BundleId,
    IReadOnlyList<AirDropFileEntry> Files,
    bool ConvertMediaFormats = false)
{
    public const string FinderBundleId = "com.apple.finder";

    public Dictionary<string, object?> ToPlist() => new()
    {
        ["SenderComputerName"] = SenderComputerName,
        ["SenderModelName"] = SenderModelName,
        ["SenderID"] = SenderId,
        ["BundleID"] = BundleId,
        ["ConvertMediaFormats"] = ConvertMediaFormats,
        ["Files"] = Files.Select(f => (object?)f.ToPlist()).ToList(),
    };

    public static AirDropAskRequest FromPlist(IReadOnlyDictionary<string, object?> plist)
    {
        var files = new List<AirDropFileEntry>();

        if (plist.GetValueOrDefault("Files") is IEnumerable<object?> entries)
        {
            foreach (object? entry in entries)
            {
                if (entry is IReadOnlyDictionary<string, object?> dict)
                    files.Add(AirDropFileEntry.FromPlist(dict));
            }
        }

        return new AirDropAskRequest(
            SenderComputerName: plist.GetValueOrDefault("SenderComputerName") as string ?? "Unknown",
            SenderModelName: plist.GetValueOrDefault("SenderModelName") as string ?? "Unknown",
            SenderId: plist.GetValueOrDefault("SenderID") as string ?? "",
            BundleId: plist.GetValueOrDefault("BundleID") as string ?? "",
            Files: files,
            ConvertMediaFormats: plist.GetValueOrDefault("ConvertMediaFormats") is true);
    }
}

/// <summary>Receiver identity, returned from both /Discover and a successful /Ask.</summary>
public sealed record AirDropReceiverIdentity(string ComputerName, string ModelName)
{
    public Dictionary<string, object?> ToPlist() => new()
    {
        ["ReceiverComputerName"] = ComputerName,
        ["ReceiverModelName"] = ModelName,
    };

    public static AirDropReceiverIdentity FromPlist(IReadOnlyDictionary<string, object?> plist) => new(
        plist.GetValueOrDefault("ReceiverComputerName") as string ?? "Unknown",
        plist.GetValueOrDefault("ReceiverModelName") as string ?? "Unknown");
}
