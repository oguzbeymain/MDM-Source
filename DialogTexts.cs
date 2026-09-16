namespace MDM
{
    /// <summary>Uygulama genelinde tutarlı onay / bilgi metinleri (Loc üzerinden).</summary>
    internal static class DialogTexts
    {
        public static string Ok => Loc.T("dialog.ok", "Tamam");
        public static string Confirm => Loc.T("dialog.confirm", "Onayla");
        public static string Dismiss => Loc.T("dialog.cancel", "İptal");
        public static string DismissAlt => Loc.T("dialog.cancel", "İptal");
        public static string Continue => Loc.T("dialog.continue", "Devam et");
        public static string DeleteConfirm => Loc.T("dialog.delete", "Sil");

        public static string CancelDownloadTitle => Loc.T("dialog.cancel_download_title", "İndirmeyi iptal et");
        public static string CancelDownloadMessage => Loc.T("dialog.cancel_download_message", "Bu indirmeyi durdurmak istiyor musunuz?");
        public static string CancelDownloadConfirm => Loc.T("dialog.cancel", "İptal");
        public static string CancelDownloadDismiss => Loc.T("dialog.continue", "Devam et");

        public static string DeleteTitle => Loc.T("dialog.delete_title", "Silme onayı");
        public static string DeleteCategoryTitle => Loc.T("dialog.delete_category_title", "Kategoriyi sil");
        public static string UnsavedSettingsTitle => Loc.T("dialog.unsaved_title", "Kaydedilmemiş değişiklikler");
        public static string UnsavedSettingsMessage => Loc.T("dialog.unsaved_message", "Kaydedilmemiş değişiklikler var. Kaydetmek ister misiniz?");
        public static string UnsavedSettingsSave => Loc.T("dialog.save", "Kaydet");
        public static string UnsavedSettingsDiscard => Loc.T("dialog.dont_save", "Kaydetme");

        public static string SelectFilesHint => Loc.T("dialog.select_files_hint", "Devam etmek için listeden en az bir dosya seçin.");

        public static string CancelDownloadDetail(string fileName) =>
            string.IsNullOrWhiteSpace(fileName) ? "" : Loc.Tf("dialog.cancel_download_detail", fileName);

        public static string DeleteMessage(string fileName) =>
            Loc.Tf("dialog.delete_message", fileName);

        public static string DeleteManyMessage(int count) =>
            Loc.Tf("dialog.delete_many", count);

        public static string DeleteDetail(bool fromDisk, int count) =>
            fromDisk
                ? Loc.T(count == 1 ? "dialog.delete_detail_disk" : "dialog.delete_detail_disk_many",
                    count == 1 ? "Dosya listeden ve diskten kaldırılır." : "Seçili dosyalar listeden ve diskten kaldırılır.")
                : Loc.T(count == 1 ? "dialog.delete_detail_list" : "dialog.delete_detail_list_many",
                    count == 1 ? "Yalnızca listeden çıkarılır; dosya diskte kalır." : "Yalnızca listeden çıkarılır; dosyalar diskte kalır.");

        public static string DeleteCategoryMessage(int count) =>
            count == 1
                ? Loc.T("dialog.delete_category_one", "Seçili kategori silinsin mi?")
                : Loc.Tf("dialog.delete_category_many", count);

        public static string DeleteCategoryDetail() =>
            Loc.T("dialog.delete_category_detail", "Kategori listeden kaldırılır; bilgisayardaki klasör de silinir.");
    }
}
