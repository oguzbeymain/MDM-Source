namespace DownloadMuck
{
    /// <summary>Uygulama genelinde tutarlı onay / bilgi metinleri.</summary>
    internal static class DialogTexts
    {
        public const string Ok = "Tamam";
        public const string Confirm = "Onayla";
        public const string Dismiss = "İptal";
        public const string DismissAlt = "İptal";
        public const string Continue = "Devam et";
        public const string DeleteConfirm = "Sil";

        public const string CancelDownloadTitle = "İndirmeyi iptal et";
        public const string CancelDownloadMessage = "Bu indirmeyi durdurmak istiyor musunuz?";
        public const string CancelDownloadConfirm = "İptal et";
        public const string CancelDownloadDismiss = "Devam et";

        public const string DeleteTitle = "Silme onayı";
        public const string DeleteCategoryTitle = "Kategoriyi sil";
        public const string UnsavedSettingsTitle = "Kaydedilmemiş değişiklikler";
        public const string UnsavedSettingsMessage = "Kaydedilmemiş değişiklikler var. Kaydetmek ister misiniz?";
        public const string UnsavedSettingsSave = "Kaydet";
        public const string UnsavedSettingsDiscard = "Kaydetme";

        public const string SelectFilesHint = "Devam etmek için listeden en az bir dosya seçin.";

        public static string CancelDownloadDetail(string fileName) =>
            string.IsNullOrWhiteSpace(fileName) ? "" : $"“{fileName}” indirme listesinden kaldırılır.";

        public static string DeleteMessage(string fileName) =>
            $"“{fileName}” silinsin mi?";

        public static string DeleteManyMessage(int count) =>
            $"{count} öğe silinsin mi?";

        public static string DeleteDetail(bool fromDisk, int count) =>
            fromDisk
                ? (count == 1
                    ? "Dosya listeden ve diskten kaldırılır."
                    : "Seçili dosyalar listeden ve diskten kaldırılır.")
                : (count == 1
                    ? "Yalnızca listeden çıkarılır; dosya diskte kalır."
                    : "Yalnızca listeden çıkarılır; dosyalar diskte kalır.");

        public static string DeleteCategoryMessage(int count) =>
            count == 1 ? "Seçili kategori silinsin mi?" : $"{count} kategori silinsin mi?";

        public static string DeleteCategoryDetail() =>
            "Kategori listeden kaldırılır; bilgisayardaki klasör de silinir.";
    }
}
