namespace MDM
{
    public static class CategoryIcons
    {
        public static readonly string[] PickerIcons =
        {
            "📁", "⚡", "🎬", "🎵", "📦", "📷", "🚀", "⭐", "💾", "🎮",
            "📚", "🔧", "💡", "🌐", "📎", "🔒", "🎯", "💼", "🏠", "❤️",
            "🔔", "📝", "🗂️", "🧩", "🎨", "🛠️", "📊", "🗃️"
        };

        public const string Folder = "📁";

        /// <summary>Segoe MDL2 denemesi sonrası kalan glifleri emojiye çevir.</summary>
        public static string Normalize(string? icon)
        {
            if (string.IsNullOrWhiteSpace(icon)) return Folder;
            if (icon.Length >= 1 && icon[0] >= '\uE700' && icon[0] <= '\uF8FF')
            {
                return icon[0] switch
                {
                    '\uE8B7' => "📁",
                    '\uE945' => "⚡",
                    '\uE714' => "🎬",
                    '\uE8D6' => "🎵",
                    '\uE7B8' => "📦",
                    '\uE91B' => "📷",
                    '\uE709' => "🚀",
                    '\uE734' => "⭐",
                    '\uE74E' => "💾",
                    '\uE7FC' => "🎮",
                    '\uE8F1' => "📚",
                    '\uE90F' => "🔧",
                    '\uEA80' => "💡",
                    '\uE774' => "🌐",
                    '\uE723' => "📎",
                    '\uE72E' => "🔒",
                    '\uE81B' => "🎯",
                    '\uE821' => "💼",
                    '\uE80F' => "🏠",
                    '\uEB52' => "❤️",
                    '\uEA8F' => "🔔",
                    '\uE8A5' => "📝",
                    '\uE8C8' => "🗂️",
                    '\uEA86' => "🧩",
                    '\uE790' => "🎨",
                    '\uEA37' => "🛠️",
                    '\uE9D2' => "📊",
                    _ => Folder
                };
            }
            return icon;
        }
    }
}
