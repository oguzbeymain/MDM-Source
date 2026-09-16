namespace MDM
{
    /// <summary>İç durum Türkçe kalır; kullanıcıya gösterilen metin Loc ile çevrilir.</summary>
    public static class StatusLocalizer
    {
        public static string ToUi(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "";
            string s = status.Trim();

            if (s.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.completed", "Tamamlandı");
            if (s.Contains("İptal", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Iptal", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.cancelled", "İptal edildi");
            if (s.Contains("Duraklat", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.paused", "Duraklatıldı");
            if (s.StartsWith("Hata", StringComparison.OrdinalIgnoreCase)
                || s.Contains(" hatası", StringComparison.OrdinalIgnoreCase)
                || s.Contains(" hatasi", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.error", "Hata");
            if (s.Contains("Kuyruk", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.queued", "Kuyrukta");
            if (s.Contains("Zamanland", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.scheduled", "Zamanlandı");
            if (s.Equals("Hazır", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Hazir", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.ready", "Hazır");
            if (s.Contains("İndiriliyor", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Indiriliyor", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Dosya bilgileri", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Tek kanaldan", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Torrent", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Magnet", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Ağ", StringComparison.OrdinalIgnoreCase)
                || s.Contains("bekleniyor", StringComparison.OrdinalIgnoreCase)
                || s.Contains("devam", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.downloading", "İndiriliyor");
            if (s.StartsWith("Kural", StringComparison.OrdinalIgnoreCase))
                return Loc.T("status.rule", "Kural");

            return s;
        }
    }
}
