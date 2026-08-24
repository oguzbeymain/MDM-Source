using Xunit;
using DownloadMuck;

namespace DownloadMuck.Tests;

public class UrlClassifierTests
{
    [Theory]
    [InlineData(null, TransferKind.Unknown)]
    [InlineData("", TransferKind.Unknown)]
    [InlineData("   ", TransferKind.Unknown)]
    [InlineData("not a url", TransferKind.Unknown)]
    [InlineData("http://localhost/file.bin", TransferKind.Http)]
    [InlineData("https://cdn.example.com/a.zip", TransferKind.Http)]
    [InlineData("https://x.com/dl?name=a.torrent", TransferKind.Http)]
    [InlineData("https://mirror.example/file.torrent", TransferKind.Torrent)]
    [InlineData(@"C:\cache\ubuntu.torrent", TransferKind.Torrent)]
    [InlineData("file:///C:/cache/ubuntu.torrent", TransferKind.Torrent)]
    [InlineData("ftp://host/a.bin", TransferKind.Ftp)]
    [InlineData("ftps://host/a.bin", TransferKind.Ftp)]
    [InlineData("sftp://host/a.bin", TransferKind.Sftp)]
    [InlineData("magnet:?xt=urn:btih:abc", TransferKind.Magnet)]
    [InlineData("movie.torrent", TransferKind.Torrent)]
    [InlineData("pack.metalink", TransferKind.Metalink)]
    [InlineData("pack.meta4", TransferKind.Metalink)]
    public void Classify_matches_scheme_and_does_not_swallow_http(string? url, TransferKind expected)
    {
        Assert.Equal(expected, UrlClassifier.Classify(url));
    }

    [Fact]
    public void CanDownloadNow_live_protocols()
    {
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Http));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Ftp));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Sftp));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Metalink));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Magnet));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Torrent));
        Assert.False(UrlClassifier.CanDownloadNow(TransferKind.Unknown));
    }

    [Fact]
    public void ExtractHttpUrls_dedupes_and_strips_punctuation()
    {
        string text = "see https://a.example/x.zip, and HTTPS://A.example/x.zip and https://b.example/y.bin.";
        var urls = UrlClassifier.ExtractHttpUrls(text);
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://a.example/x.zip", urls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://b.example/y.bin", urls);
    }

    [Fact]
    public void ExtractHttpUrls_resolves_relative_href_against_base()
    {
        string html = """
            <a href="/files/a.zip">a</a>
            <a href="../b.rar">b</a>
            <a href="javascript:void(0)">no</a>
            <a href="mailto:x@y.com">no</a>
            """;
        var urls = UrlClassifier.ExtractHttpUrls(html, "https://cdn.example.com/page/list.html");
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://cdn.example.com/files/a.zip", urls);
        Assert.Contains("https://cdn.example.com/b.rar", urls);
    }

    [Fact]
    public void ExtractHttpUrls_from_html_href()
    {
        string html = """
            <html><a href="https://cdn.example.com/one.rar">one</a>
            <a href='https://cdn.example.com/two.zip'>two</a></html>
            """;
        var urls = UrlClassifier.ExtractHttpUrls(html);
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://cdn.example.com/one.rar", urls[0]);
        Assert.Equal("https://cdn.example.com/two.zip", urls[1]);
    }

    [Fact]
    public void ExtractHttpUrls_ignores_magnet_and_ftp()
    {
        string text = "magnet:?xt=urn:btih:x ftp://h/a.bin https://ok.example/f.bin";
        var urls = UrlClassifier.ExtractHttpUrls(text);
        Assert.Single(urls);
        Assert.Equal("https://ok.example/f.bin", urls[0]);
    }

    [Fact]
    public void UnsupportedMessage_is_non_empty_for_each_kind()
    {
        foreach (TransferKind kind in Enum.GetValues<TransferKind>())
            Assert.False(string.IsNullOrWhiteSpace(UrlClassifier.UnsupportedMessage(kind)));
    }
}
