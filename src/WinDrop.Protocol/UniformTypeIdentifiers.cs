namespace WinDrop.Protocol;

/// <summary>
/// Uniform Type Identifiers for the /Ask <c>FileType</c> field.
///
/// <c>FileType</c> tells the receiver what a file is, separately from what it is called.
/// On Apple platforms it plausibly also decides where an accepted file lands — a photo
/// into Photos, everything else into Files — which makes
/// <see cref="AirDropFileEntry.DefaultFileType"/> on every file worse than vague: it is
/// the answer "an opaque byte stream", true of a JPEG and silent about it. Plausibly is
/// the load-bearing word. No Apple device has ever accepted a transfer from us, so the
/// routing claim is read and reasoned, never observed; it is an open question in
/// docs/protocol-notes.md. The identifiers below are Apple's published core types, so
/// their spelling is not in doubt, only their effect.
///
/// The type comes from the name and not from the content, which is the opposite of what
/// opendrop does: it reads the first 128 bytes of the first selected file and maps fleep's
/// verdict to a UTI. Put through fleep's whole signature table (tools/uti_oracle.py,
/// fixture opendrop-uti.json) that classifier calls a JPEG <c>public.image</c>, a Canon
/// CR2 <c>public.image</c>, a .zip <c>public.content</c> and a .gz
/// <c>public.zip-archive</c>. <c>public.jpeg</c> and <c>public.camera-raw-image</c>, both
/// named in its source, are never produced at all.
///
/// Some of that is ordinary bugs, but the raw case is not, and it is the reason not to
/// take the same approach: CR2, NEF, ARW and DNG are TIFF containers, so they open with
/// <c>II*\0</c> exactly as a plain TIFF does, and HEIC, MP4, MOV and M4A all open with an
/// <c>ftyp</c> box. Headers cannot separate the kinds of file a user considers different,
/// and those are precisely the ones whose routing would differ.
///
/// An extension does separate them, and on Windows it is not a hint but the system's own
/// notion of type — the icon, the default application and the preview we put in our own
/// /Ask all come from it. It is also already inside the <c>FileName</c> we send, so a type
/// derived from it cannot contradict the rest of the entry. And it needs no read: /Ask is
/// assembled before the user has consented to anything.
/// </summary>
public static class UniformTypeIdentifiers
{
    /// <summary>A directory, whatever it holds. The receiver is told this rather than inferring it.</summary>
    public const string Folder = "public.folder";

    /// <summary>
    /// The identifier for a file of this name, or <see cref="AirDropFileEntry.DefaultFileType"/>
    /// when its extension is not one we are prepared to name. Falling back is not a failure:
    /// an unrecognised file really is an opaque byte stream to us, and claiming otherwise
    /// would be worse than saying so.
    /// </summary>
    public static string ForFileName(string fileName) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(fileName), AirDropFileEntry.DefaultFileType);

    // Where Apple documents no concrete identifier for a format, the abstract one it would
    // conform to is used (public.movie, public.audio). That is still true, and still more
    // than public.data says. Camera raw is deliberately abstract for a second reason: the
    // per-vendor identifiers exist, but nothing here has seen one on the wire, and every
    // raw format conforms to public.camera-raw-image anyway.
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = "public.jpeg",
        [".jpeg"] = "public.jpeg",
        [".jpe"] = "public.jpeg",
        [".png"] = "public.png",
        [".gif"] = "com.compuserve.gif",
        [".tif"] = "public.tiff",
        [".tiff"] = "public.tiff",
        [".bmp"] = "com.microsoft.bmp",
        [".ico"] = "com.microsoft.ico",
        [".heic"] = "public.heic",
        [".heif"] = "public.heif",
        [".webp"] = "org.webmproject.webp",
        [".svg"] = "public.svg-image",
        [".jp2"] = "public.jpeg-2000",
        [".j2k"] = "public.jpeg-2000",
        [".jpf"] = "public.jpeg-2000",
        [".jpx"] = "public.jpeg-2000",
        [".cr2"] = "public.camera-raw-image",
        [".cr3"] = "public.camera-raw-image",
        [".nef"] = "public.camera-raw-image",
        [".nrw"] = "public.camera-raw-image",
        [".arw"] = "public.camera-raw-image",
        [".dng"] = "public.camera-raw-image",
        [".orf"] = "public.camera-raw-image",
        [".rw2"] = "public.camera-raw-image",
        [".raf"] = "public.camera-raw-image",
        [".pef"] = "public.camera-raw-image",
        [".srw"] = "public.camera-raw-image",

        // Video
        [".mp4"] = "public.mpeg-4",
        [".m4v"] = "com.apple.m4v-video",
        [".mov"] = "com.apple.quicktime-movie",
        [".avi"] = "public.avi",
        [".mpg"] = "public.mpeg",
        [".mpeg"] = "public.mpeg",
        [".3gp"] = "public.3gpp",
        [".3g2"] = "public.3gpp2",
        [".wmv"] = "com.microsoft.windows-media-wmv",
        [".mkv"] = "public.movie",
        [".webm"] = "public.movie",
        [".flv"] = "public.movie",

        // Audio
        [".mp3"] = "public.mp3",
        [".m4a"] = "public.mpeg-4-audio",
        [".aac"] = "public.aac-audio",
        [".wav"] = "com.microsoft.waveform-audio",
        [".aif"] = "public.aiff-audio",
        [".aiff"] = "public.aiff-audio",
        [".flac"] = "org.xiph.flac",
        [".wma"] = "com.microsoft.windows-media-wma",
        [".ogg"] = "public.audio",
        [".oga"] = "public.audio",

        // Documents
        [".pdf"] = "com.adobe.pdf",
        [".txt"] = "public.plain-text",
        [".rtf"] = "public.rtf",
        [".html"] = "public.html",
        [".htm"] = "public.html",
        [".csv"] = "public.comma-separated-values-text",
        [".json"] = "public.json",
        [".xml"] = "public.xml",
        [".doc"] = "com.microsoft.word.doc",
        [".docx"] = "org.openxmlformats.wordprocessingml.document",
        [".xls"] = "com.microsoft.excel.xls",
        [".xlsx"] = "org.openxmlformats.spreadsheetml.sheet",
        [".ppt"] = "com.microsoft.powerpoint.ppt",
        [".pptx"] = "org.openxmlformats.presentationml.presentation",
        [".epub"] = "org.idpf.epub-container",
        [".vcf"] = "public.vcard",
        [".ics"] = "com.apple.ical.ics",

        // Archives
        [".zip"] = "public.zip-archive",
        [".gz"] = "org.gnu.gnu-zip-archive",
        [".tgz"] = "org.gnu.gnu-zip-tar-archive",
        [".tar"] = "public.tar-archive",
        [".7z"] = "org.7-zip.7-zip-archive",
        [".rar"] = "com.rarlab.rar-archive",
    };
}
