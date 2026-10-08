using System.Text;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Web;

namespace WinDrop.Protocol.Tests;

public class MultipartReaderTests
{
    private const string Boundary = "----WebKitFormBoundaryq8Xz3";

    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n"));

    private static async Task<List<(string? Name, string? FileName, string? Type, byte[] Content)>> ReadAllAsync(Stream stream, string boundary = Boundary)
    {
        var reader = new MultipartReader(stream, boundary);
        var parts = new List<(string?, string?, string?, byte[])>();

        while (await reader.ReadNextPartAsync() is { } part)
        {
            var content = new MemoryStream();
            await part.Body.CopyToAsync(content);
            parts.Add((part.Name, part.FileName, part.ContentType, content.ToArray()));
        }

        return parts;
    }

    private static string SafariStyleForm() => $"""
        --{Boundary}
        Content-Disposition: form-data; name="from"

        iPhone
        --{Boundary}
        Content-Disposition: form-data; name="file"; filename="IMG_0001.JPG"
        Content-Type: image/jpeg

        line one
        line two
        --{Boundary}--

        """;

    [Fact]
    public async Task ReadsTextFieldsAndFiles()
    {
        var parts = await ReadAllAsync(new MemoryStream(Body(SafariStyleForm())));

        Assert.Equal(2, parts.Count);
        Assert.Equal(("from", null, null), (parts[0].Name, parts[0].FileName, parts[0].Type));
        Assert.Equal("iPhone", Encoding.UTF8.GetString(parts[0].Content));
        Assert.Equal(("file", "IMG_0001.JPG", "image/jpeg"), (parts[1].Name, parts[1].FileName, parts[1].Type));
        Assert.Equal("line one\r\nline two", Encoding.UTF8.GetString(parts[1].Content));
    }

    [Fact]
    public async Task ADelimiterSplitAcrossReadsIsStillFound()
    {
        // One byte per read: every delimiter straddles reads, the hard case for a streaming
        // search. The content holds a near-miss of the delimiter too, which has to be held
        // back until enough of it has arrived to rule it out, then passed on intact.
        string form = $"""
            --{Boundary}
            Content-Disposition: form-data; name="file"; filename="a.bin"

            before
            --{Boundary[..10]} not quite
            after
            --{Boundary}--
            """;

        var parts = await ReadAllAsync(new TrickleStream(Body(form)));

        Assert.Single(parts);
        Assert.Equal($"before\r\n--{Boundary[..10]} not quite\r\nafter", Encoding.UTF8.GetString(parts[0].Content));
    }

    [Fact]
    public async Task ContentEndingInCarriageReturnsKeepsThem()
    {
        string form = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"f\"; filename=\"x\"\r\n\r\nend\r\r\n\r\n--{Boundary}--";

        var parts = await ReadAllAsync(new TrickleStream(Encoding.ASCII.GetBytes(form)));

        Assert.Equal("end\r\r\n", Encoding.ASCII.GetString(parts[0].Content));
    }

    [Fact]
    public async Task PreambleEpilogueAndPaddingAfterTheBoundaryAreTolerated()
    {
        string form = $"ignored preamble\r\n--{Boundary}  \t\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nvalue\r\n--{Boundary}--\r\nepilogue";

        var parts = await ReadAllAsync(new MemoryStream(Encoding.ASCII.GetBytes(form)));

        Assert.Single(parts);
        Assert.Equal("value", Encoding.ASCII.GetString(parts[0].Content));
    }

    [Fact]
    public async Task ABodyCutOffInsideAPartIsAnError()
    {
        string form = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"f\"; filename=\"x\"\r\n\r\nhalf a fi";

        await Assert.ThrowsAsync<AirDropHttpException>(() => ReadAllAsync(new MemoryStream(Encoding.ASCII.GetBytes(form))));
    }

    [Fact]
    public async Task UnreadContentIsSkippedWhenTheNextPartIsAskedFor()
    {
        var reader = new MultipartReader(new MemoryStream(Body(SafariStyleForm())), Boundary);

        FormPart? first = await reader.ReadNextPartAsync();
        FormPart? second = await reader.ReadNextPartAsync();

        Assert.Equal("from", first!.Name);
        Assert.Equal("IMG_0001.JPG", second!.FileName);

        // The first part's stream reads nothing once the reader has moved on.
        Assert.Equal(0, await first.Body.ReadAsync(new byte[10]));
    }

    [Fact]
    public async Task AnOverlongHeaderLineIsRefused()
    {
        string form = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"{new string('a', 9000)}\"\r\n\r\nx\r\n--{Boundary}--";

        await Assert.ThrowsAsync<AirDropHttpException>(() => ReadAllAsync(new MemoryStream(Encoding.ASCII.GetBytes(form))));
    }

    [Fact]
    public async Task TooManyHeadersInAPartAreRefused()
    {
        string headers = string.Concat(Enumerable.Range(0, 20).Select(i => $"X-Filler-{i}: y\r\n"));
        string form = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"a\"\r\n{headers}\r\nx\r\n--{Boundary}--";

        await Assert.ThrowsAsync<AirDropHttpException>(() => ReadAllAsync(new MemoryStream(Encoding.ASCII.GetBytes(form))));
    }

    [Fact]
    public async Task FileNamesArriveAsUtf8()
    {
        string form = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"Ünïcödé 写真.heic\"\r\n\r\nx\r\n--{Boundary}--";

        var parts = await ReadAllAsync(new MemoryStream(Encoding.UTF8.GetBytes(form)));

        Assert.Equal("Ünïcödé 写真.heic", parts[0].FileName);
    }

    [Theory]
    [InlineData("form-data; name=\"file\"; filename=\"a.jpg\"", "file", "a.jpg")]
    [InlineData("form-data; name=file; filename=a.jpg", "file", "a.jpg")]
    [InlineData("form-data; name=\"file\"; filename=\"semi;colon.jpg\"", "file", "semi;colon.jpg")]
    [InlineData("form-data; name=\"file\"; filename=\"say \\\"cheese\\\".jpg\"", "file", "say \"cheese\".jpg")]
    [InlineData("form-data; name=\"file\"; filename=\"a%22b.jpg\"", "file", "a%22b.jpg")]
    [InlineData("form-data; name=\"file\"; filename=\"fallback.jpg\"; filename*=UTF-8''%E2%82%AC.jpg", "file", "€.jpg")]
    [InlineData("form-data; name=\"note\"", "note", null)]
    public void ParsesContentDisposition(string header, string? name, string? fileName)
    {
        Assert.Equal((name, fileName), MultipartReader.ParseDisposition(header));
    }

    [Fact]
    public void ADispositionOtherThanFormDataIsRefused()
    {
        Assert.Throws<AirDropHttpException>(() => MultipartReader.ParseDisposition("attachment; filename=\"a\""));
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=abc", "abc")]
    [InlineData("multipart/form-data; boundary=\"a b c\"", "a b c")]
    [InlineData("Multipart/Form-Data; charset=utf-8; BOUNDARY=xyz", "xyz")]
    [InlineData("multipart/mixed; boundary=abc", null)]
    [InlineData("application/json", null)]
    [InlineData("multipart/form-data", null)]
    [InlineData("multipart/form-data; boundary=", null)]
    [InlineData(null, null)]
    public void ReadsTheBoundary(string? contentType, string? expected)
    {
        Assert.Equal(expected, MultipartReader.BoundaryFrom(contentType));
    }

    [Fact]
    public void RefusesABoundaryLongerThanTheStandardAllows()
    {
        Assert.Null(MultipartReader.BoundaryFrom($"multipart/form-data; boundary={new string('a', 71)}"));
        Assert.Equal(70, MultipartReader.BoundaryFrom($"multipart/form-data; boundary={new string('a', 70)}")!.Length);
    }

    /// <summary>Returns one byte per read.</summary>
    internal sealed class TrickleStream(byte[] content) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= content.Length || count == 0) return 0;
            buffer[offset] = content[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= content.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = content[_position++];
            return ValueTask.FromResult(1);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
