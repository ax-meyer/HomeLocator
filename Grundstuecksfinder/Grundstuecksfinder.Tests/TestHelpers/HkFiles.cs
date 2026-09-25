using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>Tiny Hauskoordinaten files in Baden-Württemberg's dialect, zipped like the real download.</summary>
public static class HkFiles
{
    public const string Member = "adressen-bw.txt";

    public const string Header =
        "nba;oid;qua;landschl;land;regbezschl;regbez;kreisschl;kreis;gmdschl;gmd;ottschl;ott;strschl;str;hnr;adz;zone;ostwert;nordwert;postplz;postonm;postonmzus;postott;poststr;aud";

    public static string Row(double x, double y, string str, string hnr, string qua = "A", string gmd = "Testgemeinde") =>
        string.Create(CultureInfo.InvariantCulture,
            $"N;DEBWhk0100000001;{qua};08;Baden-Württemberg;3;Freiburg;17;Ortenaukreis;096;{gmd};0000;;91489;{str};{hnr};;32;{x:F3};{y:F3};;;;;;2026-07-15");

    /// <summary>The BW file: a header and the given rows, LF-separated.</summary>
    public static string Text(IEnumerable<string> rows) => string.Join('\n', rows.Prepend(Header)) + "\n";

    /// <summary>One address per <see cref="FakeAddress"/>, all of quality A.</summary>
    public static string Text(IEnumerable<FakeAddress> addresses) =>
        Text(addresses.Select(a => Row(a.X, a.Y, a.Street, a.Hnr)));

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>The file as a static server answers it, HEAD or GET: with an ETag and Last-Modified.</summary>
    public static HttpResponseMessage Response(byte[] zip, string etag = "\"4607dc5-656a14021d8ec\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 7, 15, 7, 27, 5, TimeSpan.Zero);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return response;
    }
}
