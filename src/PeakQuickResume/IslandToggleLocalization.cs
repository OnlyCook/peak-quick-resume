using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Translations for <see cref="IslandToggleButton"/>, our own big, clearly-labeled
    /// stand-in for the checkpoint mod's own tiny, unlabeled "use saved island / new
    /// island" checkbox next to its boarding-pass overlay text (see ROADMAP.md).
    /// The game's own <see cref="LocalizedText"/> table has no entries for this since
    /// it isn't vanilla UI, so we keep our own small table here, same rule as
    /// <see cref="PauseMenuLocalization"/> and <see cref="SavePickerLocalization"/>
    /// </summary>
    internal enum IslandToggleKey
    {
        Title,
        UsingSaved,
        UsingNew,
    }

    internal static class IslandToggleLocalization
    {
        // Array order MUST match LocalizedText.Language's declaration order:
        // English, French, Italian, German, SpanishSpain, SpanishLatam, BRPortuguese,
        // Russian, Ukrainian, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Polish, Turkish
        private static readonly Dictionary<IslandToggleKey, string[]> _table = new Dictionary<IslandToggleKey, string[]>
        {
            [IslandToggleKey.Title] = new[]
            {
                "ISLAND ON LOAD",
                "ÎLE AU CHARGEMENT",
                "ISOLA AL CARICAMENTO",
                "INSEL BEIM LADEN",
                "ISLA AL CARGAR",
                "ISLA AL CARGAR",
                "ILHA AO CARREGAR",
                "ОСТРОВ ПРИ ЗАГРУЗКЕ",
                "ОСТРІВ ПРИ ЗАВАНТАЖЕННІ",
                "加载时的岛屿",
                "",
                "ロード時の島",
                "로드 시 섬",
                "WYSPA PRZY WCZYTYWANIU",
                "YÜKLEMEDE ADA",
            },
            [IslandToggleKey.UsingSaved] = new[]
            {
                "SAVED ISLAND",
                "ÎLE SAUVEGARDÉE",
                "ISOLA SALVATA",
                "GESPEICHERTE INSEL",
                "ISLA GUARDADA",
                "ISLA GUARDADA",
                "ILHA SALVA",
                "СОХРАНЁННЫЙ ОСТРОВ",
                "ЗБЕРЕЖЕНИЙ ОСТРІВ",
                "已保存的岛屿",
                "",
                "セーブされた島",
                "저장된 섬",
                "ZAPISANA WYSPA",
                "KAYITLI ADA",
            },
            [IslandToggleKey.UsingNew] = new[]
            {
                "NEW ISLAND",
                "NOUVELLE ÎLE",
                "NUOVA ISOLA",
                "NEUE INSEL",
                "ISLA NUEVA",
                "ISLA NUEVA",
                "ILHA NOVA",
                "НОВЫЙ ОСТРОВ",
                "НОВИЙ ОСТРІВ",
                "新岛屿",
                "",
                "新しい島",
                "새로운 섬",
                "NOWA WYSPA",
                "YENİ ADA",
            },
        };

        public static string Get(IslandToggleKey key) => LocalizationHelper.Resolve(_table[key]);
    }
}
