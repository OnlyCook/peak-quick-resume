using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Translations for the F7 save picker's UI text (<see cref="SavePicker"/>) and the
    /// difficulty labels it (and <see cref="SaveArchive"/>) display. Same rules as
    /// <see cref="PauseMenuLocalization"/>: indexed by <c>(int)LocalizedText.Language</c>,
    /// covering every language the game itself ships; "Traditional Chinese" is left blank
    /// (not one of the game's shipped languages) and falls back to English
    ///
    /// "PEAK" (the ascent-0 difficulty name) and "Quick Resume" are left untranslated
    /// everywhere, same as a product/brand name
    /// </summary>
    internal enum PickerText
    {
        LoadSave,
        Solo,
        Coop,
        Select,
        Load,
        Delete,
        Cancel,
        DeleteConfirm,
        Played,
        Tenderfoot,
        CustomRun,
        AscentFormat, // {0} = ascent number
    }

    internal static class SavePickerLocalization
    {
        // Array order MUST match LocalizedText.Language's declaration order:
        // English, French, Italian, German, SpanishSpain, SpanishLatam, BRPortuguese,
        // Russian, Ukrainian, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Polish, Turkish
        private static readonly Dictionary<PickerText, string[]> _table = new Dictionary<PickerText, string[]>
        {
            [PickerText.LoadSave] = new[]
            {
                "Load Save", "Charger la sauvegarde", "Carica salvataggio", "Speicherstand laden",
                "Cargar partida guardada", "Cargar partida guardada", "Carregar save",
                "Загрузить сохранение", "Завантажити збереження", "加载存档", "",
                "セーブをロード", "저장 파일 불러오기", "Wczytaj zapis", "Kayıt Yükle",
            },
            [PickerText.Solo] = new[]
            {
                "Solo", "Solo", "Solo", "Solo", "Solo", "Solo", "Solo",
                "Соло", "Соло", "单人", "", "ソロ", "솔로", "Solo", "Solo",
            },
            [PickerText.Coop] = new[]
            {
                "Co-op", "Coop", "Co-op", "Koop", "Cooperativo", "Cooperativo", "Cooperativo",
                "Кооп", "Кооп", "合作", "", "協力", "협동", "Kooperacja", "Co-op",
            },
            [PickerText.Select] = new[]
            {
                "Select", "Sélectionner", "Seleziona", "Auswählen", "Seleccionar", "Seleccionar",
                "Selecionar", "Выбор", "Вибір", "选择", "", "選択", "선택", "Wybierz", "Seç",
            },
            [PickerText.Load] = new[]
            {
                "Load", "Charger", "Carica", "Laden", "Cargar", "Cargar", "Carregar",
                "Загрузить", "Завантажити", "加载", "", "ロード", "불러오기", "Wczytaj", "Yükle",
            },
            [PickerText.Delete] = new[]
            {
                "Delete", "Supprimer", "Elimina", "Löschen", "Eliminar", "Eliminar", "Excluir",
                "Удалить", "Видалити", "删除", "", "削除", "삭제", "Usuń", "Sil",
            },
            [PickerText.Cancel] = new[]
            {
                "Cancel", "Annuler", "Annulla", "Abbrechen", "Cancelar", "Cancelar", "Cancelar",
                "Отмена", "Скасувати", "取消", "", "キャンセル", "취소", "Anuluj", "İptal",
            },
            [PickerText.DeleteConfirm] = new[]
            {
                "Press Del again to permanently remove this save.",
                "Appuyez à nouveau sur Suppr pour supprimer définitivement cette sauvegarde.",
                "Premi di nuovo Canc per eliminare definitivamente questo salvataggio.",
                "Drücke erneut Entf, um diesen Speicherstand endgültig zu löschen.",
                "Pulsa Supr de nuevo para eliminar permanentemente esta partida guardada.",
                "Presiona Supr de nuevo para eliminar permanentemente esta partida guardada.",
                "Pressione Delete novamente para excluir permanentemente este save.",
                "Нажмите Delete ещё раз, чтобы навсегда удалить это сохранение.",
                "Натисніть Delete ще раз, щоб остаточно видалити це збереження.",
                "再次按 Delete 键将永久删除此存档。",
                "",
                "もう一度Deleteキーを押すと、このセーブデータを完全に削除します。",
                "Delete 키를 한 번 더 누르면 이 저장 파일이 영구적으로 삭제됩니다.",
                "Naciśnij ponownie Delete, aby trwale usunąć ten zapis.",
                "Bu kaydı kalıcı olarak silmek için tekrar Delete tuşuna basın.",
            },
            [PickerText.Played] = new[]
            {
                "played", "jouées", "giocate", "gespielt", "jugadas", "jugadas", "jogadas",
                "наиграно", "зіграно", "游玩时长", "", "プレイ時間", "플레이함", "rozegrane", "oynandı",
            },
            [PickerText.Tenderfoot] = new[]
            {
                "Tenderfoot", "Débutant", "Principiante", "Anfänger", "Novato", "Novato", "Novato",
                "Новичок", "Новачок", "新手", "", "初心者", "초보자", "Nowicjusz", "Acemi",
            },
            [PickerText.CustomRun] = new[]
            {
                "Custom Run", "Partie personnalisée", "Partita personalizzata", "Benutzerdefinierter Lauf",
                "Partida personalizada", "Partida personalizada", "Corrida personalizada",
                "Пользовательский забег", "Користувацький забіг", "自定义局", "",
                "カスタムラン", "커스텀 런", "Własny przebieg", "Özel Koşu",
            },
            [PickerText.AscentFormat] = new[]
            {
                "Ascent {0}", "Ascension {0}", "Ascensione {0}", "Aufstieg {0}", "Ascenso {0}",
                "Ascenso {0}", "Ascensão {0}", "Восхождение {0}", "Сходження {0}", "攀登 {0}", "",
                "アセント {0}", "어센트 {0}", "Wspinaczka {0}", "Çıkış {0}",
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(PickerText key) => LocalizationHelper.Resolve(_table[key]);
    }
}
