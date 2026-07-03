using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Translations for the custom pause-menu buttons injected by <see cref="PauseMenuPatch"/>.
    /// The game's own <see cref="LocalizedText"/> table only has entries the developers wrote,
    /// our button text doesn't exist in it, so we keep our own small table here instead
    ///
    /// Indexed by <c>(int)LocalizedText.Language</c>, covering every language the game itself
    /// currently ships (per its own settings menu): English, French, Italian, German, Spanish
    /// (Spain), Spanish (Latin America), Portuguese (Brazil), Russian, Ukrainian, Simplified
    /// Chinese, Japanese, Korean, Polish, Turkish. "Traditional Chinese" exists as an enum value
    /// but isn't one of the game's shipped languages, left blank here, falls back to English,
    /// same behavior as the game's own <c>LocalizedText.GetText</c> for a missing translation
    ///
    /// "Board Flight" instead reuses the game's own official "BOARDFLIGHT" string (the same
    /// text shown when interacting with the kiosk directly), so it's guaranteed to match
    /// </summary>
    internal enum ButtonLabel
    {
        Restart,
        ReturnToAirport,
    }

    /// <summary>The confirm-dialog sentences shown when clicking Restart / Return to Airport</summary>
    internal enum ConfirmDialog
    {
        Restart,
        ReturnToAirport,
    }

    internal static class PauseMenuLocalization
    {
        // Array order MUST match LocalizedText.Language's declaration order:
        // English, French, Italian, German, SpanishSpain, SpanishLatam, BRPortuguese,
        // Russian, Ukrainian, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Polish, Turkish
        private static readonly Dictionary<ButtonLabel, string[]> _table = new Dictionary<ButtonLabel, string[]>
        {
            [ButtonLabel.Restart] = new[]
            {
                "RESTART",           // English
                "REDÉMARRER",        // French
                "RIAVVIA",           // Italian
                "NEUSTART",          // German
                "REINICIAR",         // Spanish (Spain)
                "REINICIAR",         // Spanish (Latin America)
                "REINICIAR",         // Portuguese (Brazil)
                "ПЕРЕЗАПУСК",        // Russian
                "ПЕРЕЗАПУСК",        // Ukrainian
                "重新开始",            // Simplified Chinese
                "",                  // Traditional Chinese (unsupported, falls back to English)
                "リスタート",          // Japanese
                "재시작",              // Korean
                "RESTART",           // Polish (commonly used as-is in games/tech)
                "YENİDEN BAŞLAT",    // Turkish
            },
            [ButtonLabel.ReturnToAirport] = new[]
            {
                "RETURN TO AIRPORT",         // English
                "RETOUR À L'AÉROPORT",       // French
                "TORNA ALL'AEROPORTO",       // Italian
                "ZURÜCK ZUM FLUGHAFEN",      // German
                "VOLVER AL AEROPUERTO",      // Spanish (Spain)
                "VOLVER AL AEROPUERTO",      // Spanish (Latin America)
                "VOLTAR AO AEROPORTO",       // Portuguese (Brazil)
                "ВЕРНУТЬСЯ В АЭРОПОРТ",      // Russian
                "ПОВЕРНУТИСЯ В АЕРОПОРТ",    // Ukrainian
                "返回机场",                    // Simplified Chinese
                "",                          // Traditional Chinese (unsupported, falls back to English)
                "空港に戻る",                  // Japanese
                "공항으로 돌아가기",            // Korean
                "WRÓĆ NA LOTNISKO",          // Polish
                "HAVAALANINA DÖN",           // Turkish
            },
        };

        private static readonly Dictionary<ConfirmDialog, string[]> _dialogTable = new Dictionary<ConfirmDialog, string[]>
        {
            [ConfirmDialog.Restart] = new[]
            {
                "Restart this run? Everyone will return to the Airport and a fresh run of the same difficulty will start immediately (no checkpoint will be loaded).", // English
                "Recommencer cette partie ? Tout le monde retournera à l'aéroport et une nouvelle partie de la même difficulté commencera immédiatement (aucune sauvegarde ne sera chargée).", // French
                "Riavviare questa partita? Tutti torneranno all'aeroporto e una nuova partita della stessa difficoltà inizierà immediatamente (nessun checkpoint verrà caricato).", // Italian
                "Diesen Lauf neu starten? Alle kehren zum Flughafen zurück und ein neuer Lauf mit demselben Schwierigkeitsgrad beginnt sofort (es wird kein Speicherstand geladen).", // German
                "¿Reiniciar esta partida? Todos volverán al aeropuerto y comenzará de inmediato una nueva partida de la misma dificultad (no se cargará ningún punto de guardado).", // Spanish (Spain)
                "¿Reiniciar esta partida? Todos volverán al aeropuerto y comenzará de inmediato una nueva partida de la misma dificultad (no se cargará ningún punto de guardado).", // Spanish (Latin America)
                "Reiniciar esta corrida? Todos vão voltar para o Aeroporto e uma nova corrida da mesma dificuldade vai começar imediatamente (nenhum checkpoint será carregado).", // Portuguese (Brazil)
                "Перезапустить этот забег? Все вернутся в аэропорт, и сразу начнётся новый забег той же сложности (сохранение не будет загружено).", // Russian
                "Перезапустити цей забіг? Усі повернуться в аеропорт, і одразу почнеться новий забіг тієї ж складності (збереження не буде завантажено).", // Ukrainian
                "重新开始本局游戏?所有人将返回机场,并立即开始相同难度的新一局(不会加载任何存档)。", // Simplified Chinese
                "", // Traditional Chinese (unsupported, falls back to English)
                "このランを再開始しますか?全員が空港に戻り、同じ難易度の新しいランがすぐに始まります(チェックポイントは読み込まれません)。", // Japanese
                "이번 런을 재시작하시겠습니까? 모두 공항으로 돌아가고 동일한 난이도의 새로운 런이 즉시 시작됩니다 (체크포인트는 불러오지 않습니다).", // Korean
                "Zrestartować ten przebieg? Wszyscy wrócą na lotnisko i natychmiast rozpocznie się nowy przebieg tej samej trudności (żaden zapis nie zostanie wczytany).", // Polish
                "Bu koşuyu yeniden başlat? Herkes Havaalanı'na dönecek ve aynı zorlukta yeni bir koşu hemen başlayacak (herhangi bir kayıt yüklenmeyecek).", // Turkish
            },
            [ConfirmDialog.ReturnToAirport] = new[]
            {
                "Return everyone to the Airport now?", // English
                "Renvoyer tout le monde à l'aéroport maintenant ?", // French
                "Riportare tutti all'aeroporto ora?", // Italian
                "Jetzt alle zum Flughafen zurückbringen?", // German
                "¿Volver todos al aeropuerto ahora?", // Spanish (Spain)
                "¿Volver todos al aeropuerto ahora?", // Spanish (Latin America)
                "Voltar todos para o Aeroporto agora?", // Portuguese (Brazil)
                "Вернуть всех в аэропорт сейчас?", // Russian
                "Повернути всіх в аеропорт зараз?", // Ukrainian
                "现在让所有人返回机场?", // Simplified Chinese
                "", // Traditional Chinese (unsupported, falls back to English)
                "今すぐ全員を空港に戻しますか?", // Japanese
                "지금 모두를 공항으로 돌려보내시겠습니까?", // Korean
                "Odesłać teraz wszystkich na lotnisko?", // Polish
                "Herkes şimdi Havaalanı'na döndürülsün mü?", // Turkish
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(ButtonLabel label) => Resolve(_table[label]);

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(ConfirmDialog dialog) => Resolve(_dialogTable[dialog]);

        private static string Resolve(string[] arr)
        {
            int idx = (int)LocalizedText.CURRENT_LANGUAGE;
            if (idx >= 0 && idx < arr.Length && !string.IsNullOrEmpty(arr[idx]))
                return arr[idx];
            return arr[0]; // English fallback
        }
    }
}
