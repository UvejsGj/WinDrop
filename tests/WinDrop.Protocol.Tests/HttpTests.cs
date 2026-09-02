using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Tls;
using Xunit;

namespace WinDrop.Protocol.Tests;

public class HttpParsingTests
{
    private static HttpConnection Over(string raw) =>
        new(new MemoryStream(Encoding.ASCII.GetBytes(raw)));

    private static HttpConnection Over(byte[] raw) => new(new MemoryStream(raw));

    [Fact]
    public async Task Reads_a_request_line_and_headers()
    {
        await using var c = Over("POST /Ask HTTP/1.1\r\nHost: airdrop\r\nContent-Length: 0\r\n\r\n");

        HttpRequestHead? head = await c.ReadRequestHeadAsync();

        Assert.NotNull(head);
        Assert.Equal("POST", head.Method);
        Assert.Equal("/Ask", head.Target);
        Assert.Equal("airdrop", head.Headers["Host"]);
        Assert.Equal(0, head.Headers.ContentLength);
    }

    [Fact]
    public async Task Header_lookup_is_case_insensitive()
    {
        await using var c = Over("GET / HTTP/1.1\r\nCoNtEnT-tYpE: application/octet-stream\r\n\r\n");
        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;

        Assert.Equal("application/octet-stream", head.Headers["content-type"]);
    }

    [Fact]
    public async Task Clean_close_before_a_request_returns_null()
    {
        await using var c = Over("");
        Assert.Null(await c.ReadRequestHeadAsync());
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET / HTTP/2.0\r\n\r\n")]
    public async Task Malformed_request_lines_are_rejected(string raw)
    {
        await using var c = Over(raw);
        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadRequestHeadAsync());
    }

    [Fact]
    public async Task Obsolete_line_folding_is_rejected()
    {
        // obs-fold is a classic request-smuggling primitive; refuse rather than rejoin.
        await using var c = Over("GET / HTTP/1.1\r\nX-A: one\r\n continued\r\n\r\n");
        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadRequestHeadAsync());
    }

    [Fact]
    public async Task Absurd_header_count_is_rejected()
    {
        var raw = new StringBuilder("GET / HTTP/1.1\r\n");
        for (int i = 0; i < 500; i++) raw.Append($"X-{i}: v\r\n");
        raw.Append("\r\n");

        await using var c = Over(raw.ToString());
        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadRequestHeadAsync());
    }

    [Theory]
    [InlineData("0, 100")]
    [InlineData("+5")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task Malformed_content_length_is_rejected(string value)
    {
        await using var c = Over($"POST / HTTP/1.1\r\nContent-Length: {value}\r\n\r\n");
        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;

        Assert.Throws<AirDropHttpException>(() => head.Headers.ContentLength);
    }

    [Fact]
    public async Task Reads_a_content_length_body_and_stops_at_the_boundary()
    {
        // A second request follows; the body stream must not swallow it.
        await using var c = Over(
            "POST /a HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello"
            + "POST /b HTTP/1.1\r\nContent-Length: 0\r\n\r\n");

        HttpRequestHead first = (await c.ReadRequestHeadAsync())!;
        byte[] body = await c.ReadBodyAsync(first.Headers, 1024);
        Assert.Equal("hello", Encoding.ASCII.GetString(body));

        HttpRequestHead? second = await c.ReadRequestHeadAsync();
        Assert.Equal("/b", second!.Target);
    }

    [Fact]
    public async Task Reads_a_chunked_body()
    {
        await using var c = Over(
            "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");

        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;
        Assert.True(head.Headers.IsChunked);

        byte[] body = await c.ReadBodyAsync(head.Headers, 1024);
        Assert.Equal("hello world", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task Chunk_extensions_are_ignored()
    {
        await using var c = Over(
            "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "5;name=value\r\nhello\r\n0\r\n\r\n");

        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;
        Assert.Equal("hello", Encoding.ASCII.GetString(await c.ReadBodyAsync(head.Headers, 1024)));
    }

    [Fact]
    public async Task Chunked_body_leaves_the_connection_reusable()
    {
        await using var c = Over(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n"
            + "POST /b HTTP/1.1\r\nContent-Length: 0\r\n\r\n");

        HttpRequestHead first = (await c.ReadRequestHeadAsync())!;
        Assert.Equal("abc", Encoding.ASCII.GetString(await c.ReadBodyAsync(first.Headers, 1024)));

        Assert.Equal("/b", (await c.ReadRequestHeadAsync())!.Target);
    }

    [Fact]
    public async Task Malformed_chunk_size_is_rejected()
    {
        await using var c = Over("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n");
        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;

        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadBodyAsync(head.Headers, 1024));
    }

    [Fact]
    public async Task Truncated_body_is_rejected()
    {
        await using var c = Over("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\nshort");
        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;

        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadBodyAsync(head.Headers, 1024));
    }

    [Fact]
    public async Task Oversized_body_is_refused_at_the_limit()
    {
        await using var c = Over("POST / HTTP/1.1\r\nContent-Length: 100\r\n\r\n" + new string('x', 100));
        HttpRequestHead head = (await c.ReadRequestHeadAsync())!;

        await Assert.ThrowsAsync<AirDropHttpException>(() => c.ReadBodyAsync(head.Headers, 10));
    }

    [Fact]
    public async Task Header_values_containing_line_breaks_are_refused_on_write()
    {
        // Otherwise a peer-supplied value could inject a whole extra message.
        await using var c = new HttpConnection(new MemoryStream());
        var headers = new HttpHeaders();
        headers.Add("X-Evil", "value\r\nX-Injected: yes");

        await Assert.ThrowsAsync<AirDropHttpException>(
            () => c.WriteRequestAsync("GET", "/", headers, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task Reads_a_status_line()
    {
        await using var c = Over("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        HttpResponseHead head = await c.ReadResponseHeadAsync();

        Assert.Equal(200, head.StatusCode);
        Assert.Equal("OK", head.ReasonPhrase);
        Assert.True(head.IsSuccess);
    }
}

/// <summary>
/// End-to-end over a real TCP socket with real TLS, because the properties that matter
/// — that a self-signed certificate is accepted, and that two requests survive on one
/// connection — only exist once an actual handshake has happened.
/// </summary>
public class TlsHttpLoopbackTests
{
    private static async Task RunAsync(
        Func<HttpConnection, Task> server,
        Func<HttpConnection, Task> client)
    {
        using X509Certificate2 serverCert = AirDropCertificate.CreateSelfSigned("WinDrop-Receiver");
        using X509Certificate2 clientCert = AirDropCertificate.CreateSelfSigned("WinDrop-Sender");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var ssl = await AirDropTls.AuthenticateAsServerAsync(accepted.GetStream(), serverCert);
            await using var connection = new HttpConnection(ssl, ownsStream: false);
            await server(connection);
        });

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);

            await using var ssl = await AirDropTls.AuthenticateAsClientAsync(tcp.GetStream(), clientCert);
            await using var connection = new HttpConnection(ssl, ownsStream: false);
            await client(connection);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Self_signed_certificates_complete_a_handshake_in_both_directions()
    {
        string? seenBody = null;

        await RunAsync(
            async server =>
            {
                HttpRequestHead head = (await server.ReadRequestHeadAsync())!;
                seenBody = Encoding.UTF8.GetString(await server.ReadBodyAsync(head.Headers, 4096));
                await server.WriteResponseAsync(200, "OK", new HttpHeaders(), "pong"u8.ToArray());
            },
            async client =>
            {
                await client.WriteRequestAsync("POST", "/Discover", new HttpHeaders(), "ping"u8.ToArray());

                HttpResponseHead response = await client.ReadResponseHeadAsync();
                Assert.Equal(200, response.StatusCode);
                Assert.Equal("pong", Encoding.UTF8.GetString(await client.ReadBodyAsync(response.Headers, 4096)));
            });

        Assert.Equal("ping", seenBody);
    }

    [Fact]
    public async Task Two_requests_share_one_connection()
    {
        // The property AirDrop actually depends on: the receiver ties the user's consent
        // decision to the connection it was granted on, so /Ask and /Upload must arrive
        // on the same one. This is why HttpClient is not usable here.
        var targets = new List<string>();

        await RunAsync(
            async server =>
            {
                for (int i = 0; i < 2; i++)
                {
                    HttpRequestHead head = (await server.ReadRequestHeadAsync())!;
                    targets.Add(head.Target);
                    await server.ReadBodyAsync(head.Headers, 4096);
                    await server.WriteResponseAsync(200, "OK", new HttpHeaders(), ReadOnlyMemory<byte>.Empty);
                }
            },
            async client =>
            {
                foreach (string target in new[] { "/Ask", "/Upload" })
                {
                    await client.WriteRequestAsync("POST", target, new HttpHeaders(), "x"u8.ToArray());
                    HttpResponseHead response = await client.ReadResponseHeadAsync();
                    await client.ReadBodyAsync(response.Headers, 4096);
                }
            });

        Assert.Equal(["/Ask", "/Upload"], targets);
    }

    [Fact]
    public async Task Streams_a_large_chunked_body_without_buffering_it_whole()
    {
        // Stands in for /Upload, where the archive is produced on the fly and its length
        // is not known before the first byte goes out.
        const int chunkSize = 64 * 1024;
        const int chunkCount = 24;
        long received = 0;

        await RunAsync(
            async server =>
            {
                HttpRequestHead head = (await server.ReadRequestHeadAsync())!;
                Assert.True(head.Headers.IsChunked);

                await using Stream body = server.OpenBody(head.Headers);
                var buffer = new byte[16 * 1024];

                while (true)
                {
                    int read = await body.ReadAsync(buffer);
                    if (read == 0) break;
                    received += read;
                }

                await server.WriteResponseAsync(200, "OK", new HttpHeaders(), ReadOnlyMemory<byte>.Empty);
            },
            async client =>
            {
                await client.WriteStreamingRequestAsync(
                    "POST", "/Upload", new HttpHeaders(),
                    async (body, ct) =>
                    {
                        var payload = new byte[chunkSize];
                        Random.Shared.NextBytes(payload);

                        for (int i = 0; i < chunkCount; i++)
                            await body.WriteAsync(payload, ct);
                    });

                HttpResponseHead response = await client.ReadResponseHeadAsync();
                Assert.Equal(200, response.StatusCode);
            });

        Assert.Equal((long)chunkSize * chunkCount, received);
    }
}
