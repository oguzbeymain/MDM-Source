using System.Text.Json.Serialization;

namespace MDM
{
    public sealed class ExtCaptureRequest
    {
        public string Url { get; set; } = "";
        public string Filename { get; set; } = "";
        public string Mime { get; set; } = "";
        public string PageUrl { get; set; } = "";
        public string Referrer { get; set; } = "";
        public string Kind { get; set; } = "";
        public string FormatId { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>Seçilen kalitenin yaklaşık boyutu (byte); yoksa 0.</summary>
        public long Filesize { get; set; }
        public string Cookies { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FormatsRequest
    {
        public string PageUrl { get; set; } = "";
        public string MediaUrl { get; set; } = "";
        public List<string> Candidates { get; set; } = new();
        /// <summary>Eklentinin sniff ettiği playlist gövdesi (IDM modeli).</summary>
        public string PlaylistBody { get; set; } = "";
        public string Cookies { get; set; } = "";
        public string Referrer { get; set; } = "";
        public string Site { get; set; } = "";
        public string Title { get; set; } = "";
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FormatOptionDto
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public int Height { get; set; }
        public int Width { get; set; }
        public int Fps { get; set; }
        public string Vcodec { get; set; } = "";
        public string Acodec { get; set; } = "";
        public long Bandwidth { get; set; }
        public long? Filesize { get; set; }
        public string Url { get; set; } = "";
        public string Type { get; set; } = "";
        public string Kind { get; set; } = "";
        public string FormatId { get; set; } = "";
        public string? AudioUrl { get; set; }
        public string? MasterUrl { get; set; }
        public string? MpdUrl { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FormatsResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string Title { get; set; } = "";
        public string? Thumbnail { get; set; }
        public bool Protected { get; set; }
        public bool YtDlpSuggested { get; set; }
        public List<FormatOptionDto> Formats { get; set; } = new();
    }
}
