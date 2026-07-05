using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Translations for every on-screen message OUR code triggers via
    /// <see cref="CheckpointInterop.TryShowMessage"/> (from <see cref="Plugin"/>,
    /// <see cref="ResumeOrchestrator"/>, <see cref="RestartOrchestrator"/>), plus two
    /// that override the checkpoint mod's own English-only strings at their point of
    /// use: <see cref="MsgKey.LoadingSavegame"/> ("Loading savegame..." on its loading
    /// screen, see <see cref="LoadingScreenPatch"/>) and <see cref="MsgKey.SavegameLoaded"/>
    /// ("Save game loaded!" once a load finishes, see <see cref="SavegameLoadedMessagePatch"/>)
    ///
    /// We localize those two because they fire as a direct result of our own load/resume
    /// flow just as often as the checkpoint mod's own F6 key, so leaving them English-only
    /// would be a jarring language switch mid-flow. We do NOT localize anything else in
    /// the checkpoint mod, only these two shared overlays
    ///
    /// Same indexing/fallback rule as <see cref="PauseMenuLocalization"/> and
    /// <see cref="SavePickerLocalization"/>
    /// </summary>
    internal enum MsgKey
    {
        OnlyHostResume,
        LoadIntoGameFirst,
        QuickResumeStarting,
        NoSaveCustom,
        NoSaveDifficulty, // {0} = ascent number
        StartingFreshRun,
        WaitingForPlayers,
        PlayersTimedOut,
        SaveLoadedWelcomeBack,
        OnlyHostRestart,
        RestartingRun,
        RunRestarted,
        RestartFailed,
        NoSavesSolo,
        NoSavesCoop,
        LoadingSavegame,
        SavegameLoaded,
        TeleportBugHint,
    }

    internal static class MessagesLocalization
    {
        // Array order MUST match LocalizedText.Language's declaration order:
        // English, French, Italian, German, SpanishSpain, SpanishLatam, BRPortuguese,
        // Russian, Ukrainian, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Polish, Turkish
        private static readonly Dictionary<MsgKey, string[]> _table = new Dictionary<MsgKey, string[]>
        {
            [MsgKey.OnlyHostResume] = new[]
            {
                "Only the host can resume the save!",
                "Seul l'hôte peut reprendre la sauvegarde !",
                "Solo l'host può riprendere la partita salvata!",
                "Nur der Host kann den Spielstand fortsetzen!",
                "¡Solo el host puede reanudar la partida guardada!",
                "¡Solo el host puede reanudar la partida guardada!",
                "Somente o host pode retomar o save!",
                "Только хост может продолжить сохранённую игру!",
                "Лише хост може відновити збережену гру!",
                "只有房主才能恢复存档!",
                "",
                "セーブデータの再開はホストのみ可能です!",
                "호스트만 저장된 게임을 재개할 수 있습니다!",
                "Tylko host może wznowić zapisaną grę!",
                "Kayıtlı oyunu yalnızca host devam ettirebilir!",
            },
            [MsgKey.LoadIntoGameFirst] = new[]
            {
                "Load into the game first.",
                "Chargez d'abord une partie.",
                "Prima entra in una partita.",
                "Lade zuerst ein Spiel.",
                "Primero carga una partida.",
                "Primero carga una partida.",
                "Primeiro entre em uma partida.",
                "Сначала загрузитесь в игру.",
                "Спочатку завантажтеся в гру.",
                "请先进入游戏。",
                "",
                "まずゲームに入ってください。",
                "먼저 게임에 접속하세요.",
                "Najpierw wejdź do gry.",
                "Önce oyuna girin.",
            },
            [MsgKey.QuickResumeStarting] = new[]
            {
                "Quick Resume: starting...",
                "Reprise rapide : démarrage...",
                "Ripresa rapida: avvio in corso...",
                "Quick Resume: wird gestartet...",
                "Reanudación rápida: iniciando...",
                "Reanudación rápida: iniciando...",
                "Retomada rápida: iniciando...",
                "Быстрое возобновление: запуск...",
                "Швидке відновлення: запуск...",
                "快速续玩:启动中...",
                "",
                "クイックレジューム: 開始中...",
                "퀵 레주메: 시작 중...",
                "Szybkie wznowienie: uruchamianie...",
                "Hızlı Devam: başlatılıyor...",
            },
            [MsgKey.NoSaveCustom] = new[]
            {
                "No save found for this custom run.",
                "Aucune sauvegarde trouvée pour cette partie personnalisée.",
                "Nessun salvataggio trovato per questa partita personalizzata.",
                "Kein Speicherstand für diesen benutzerdefinierten Lauf gefunden.",
                "No se encontró ninguna partida guardada para esta partida personalizada.",
                "No se encontró ninguna partida guardada para esta partida personalizada.",
                "Nenhum save encontrado para esta corrida personalizada.",
                "Сохранение для этого пользовательского забега не найдено.",
                "Збереження для цього користувацького забігу не знайдено.",
                "未找到该自定义局的存档。",
                "",
                "このカスタムランのセーブデータが見つかりません。",
                "이 커스텀 런에 대한 저장 파일을 찾을 수 없습니다.",
                "Nie znaleziono zapisu dla tego własnego przebiegu.",
                "Bu özel koşu için kayıt bulunamadı.",
            },
            [MsgKey.NoSaveDifficulty] = new[]
            {
                "No save found for this difficulty (ascent {0}).",
                "Aucune sauvegarde trouvée pour cette difficulté (ascension {0}).",
                "Nessun salvataggio trovato per questa difficoltà (ascensione {0}).",
                "Kein Speicherstand für diesen Schwierigkeitsgrad gefunden (Aufstieg {0}).",
                "No se encontró ninguna partida guardada para esta dificultad (ascenso {0}).",
                "No se encontró ninguna partida guardada para esta dificultad (ascenso {0}).",
                "Nenhum save encontrado para esta dificuldade (ascensão {0}).",
                "Сохранение для этой сложности не найдено (восхождение {0}).",
                "Збереження для цієї складності не знайдено (сходження {0}).",
                "未找到该难度的存档(攀登 {0})。",
                "",
                "この難易度のセーブデータが見つかりません (アセント {0})。",
                "이 난이도에 대한 저장 파일을 찾을 수 없습니다 (어센트 {0}).",
                "Nie znaleziono zapisu dla tego poziomu trudności (wspinaczka {0}).",
                "Bu zorluk için kayıt bulunamadı (çıkış {0}).",
            },
            [MsgKey.StartingFreshRun] = new[]
            {
                "Starting a fresh run of your saved difficulty...",
                "Démarrage d'une nouvelle partie de votre difficulté sauvegardée...",
                "Avvio di una nuova partita alla tua difficoltà salvata...",
                "Ein neuer Lauf deines gespeicherten Schwierigkeitsgrads wird gestartet...",
                "Iniciando una nueva partida de tu dificultad guardada...",
                "Iniciando una nueva partida de tu dificultad guardada...",
                "Iniciando uma nova corrida na sua dificuldade salva...",
                "Запускается новый забег вашей сохранённой сложности...",
                "Запускається новий забіг вашої збереженої складності...",
                "正在以你保存的难度开始新的一局...",
                "",
                "保存された難易度で新しいランを開始しています...",
                "저장된 난이도로 새로운 런을 시작합니다...",
                "Rozpoczynanie nowego przebiegu na zapisanym poziomie trudności...",
                "Kayıtlı zorluğunuzla yeni bir koşu başlatılıyor...",
            },
            [MsgKey.WaitingForPlayers] = new[]
            {
                "Waiting for other players to load...",
                "En attente du chargement des autres joueurs...",
                "In attesa che gli altri giocatori carichino...",
                "Warte, bis andere Spieler geladen haben...",
                "Esperando a que otros jugadores carguen...",
                "Esperando a que otros jugadores carguen...",
                "Aguardando outros jogadores carregarem...",
                "Ожидание загрузки других игроков...",
                "Очікування завантаження інших гравців...",
                "正在等待其他玩家加载...",
                "",
                "他のプレイヤーの読み込みを待っています...",
                "다른 플레이어의 로딩을 기다리는 중...",
                "Oczekiwanie na wczytanie innych graczy...",
                "Diğer oyuncuların yüklenmesi bekleniyor...",
            },
            [MsgKey.PlayersTimedOut] = new[]
            {
                "Some players didn't finish loading in time. Try again.",
                "Certains joueurs n'ont pas fini de charger à temps. Réessayez.",
                "Alcuni giocatori non hanno terminato il caricamento in tempo. Riprova.",
                "Einige Spieler haben das Laden nicht rechtzeitig abgeschlossen. Versuche es erneut.",
                "Algunos jugadores no terminaron de cargar a tiempo. Inténtalo de nuevo.",
                "Algunos jugadores no terminaron de cargar a tiempo. Inténtalo de nuevo.",
                "Alguns jogadores não terminaram de carregar a tempo. Tente novamente.",
                "Некоторые игроки не успели загрузиться. Попробуйте снова.",
                "Деякі гравці не встигли завантажитися. Спробуйте ще раз.",
                "部分玩家未能及时加载完成,请重试。",
                "",
                "一部のプレイヤーの読み込みが間に合いませんでした。もう一度お試しください。",
                "일부 플레이어가 제시간에 로딩을 마치지 못했습니다. 다시 시도하세요.",
                "Niektórzy gracze nie zdążyli się wczytać. Spróbuj ponownie.",
                "Bazı oyuncular zamanında yüklenemedi. Tekrar deneyin.",
            },
            [MsgKey.SaveLoadedWelcomeBack] = new[]
            {
                "Save loaded. Welcome back!",
                "Sauvegarde chargée. Bon retour !",
                "Salvataggio caricato. Bentornato!",
                "Spielstand geladen. Willkommen zurück!",
                "Partida cargada. ¡Bienvenido de nuevo!",
                "Partida cargada. ¡Bienvenido de nuevo!",
                "Save carregado. Bem-vindo de volta!",
                "Сохранение загружено. С возвращением!",
                "Збереження завантажено. З поверненням!",
                "存档加载完成,欢迎回来!",
                "",
                "セーブデータを読み込みました。おかえりなさい!",
                "저장 파일을 불러왔습니다. 다시 오신 것을 환영합니다!",
                "Zapis wczytany. Witaj z powrotem!",
                "Kayıt yüklendi. Tekrar hoş geldin!",
            },
            [MsgKey.OnlyHostRestart] = new[]
            {
                "Only the host can restart the run!",
                "Seul l'hôte peut redémarrer la partie !",
                "Solo l'host può riavviare la partita!",
                "Nur der Host kann den Lauf neu starten!",
                "¡Solo el host puede reiniciar la partida!",
                "¡Solo el host puede reiniciar la partida!",
                "Somente o host pode reiniciar a corrida!",
                "Только хост может перезапустить забег!",
                "Лише хост може перезапустити забіг!",
                "只有房主才能重新开始本局!",
                "",
                "ランの再開始はホストのみ可能です!",
                "호스트만 런을 재시작할 수 있습니다!",
                "Tylko host może zrestartować przebieg!",
                "Koşuyu yalnızca host yeniden başlatabilir!",
            },
            [MsgKey.RestartingRun] = new[]
            {
                "Restarting run...",
                "Redémarrage de la partie...",
                "Riavvio della partita...",
                "Lauf wird neu gestartet...",
                "Reiniciando partida...",
                "Reiniciando partida...",
                "Reiniciando corrida...",
                "Перезапуск забега...",
                "Перезапуск забігу...",
                "正在重新开始...",
                "",
                "ランを再開始しています...",
                "런을 재시작하는 중...",
                "Restartowanie przebiegu...",
                "Koşu yeniden başlatılıyor...",
            },
            [MsgKey.RunRestarted] = new[]
            {
                "Run restarted!",
                "Partie redémarrée !",
                "Partita riavviata!",
                "Lauf neu gestartet!",
                "¡Partida reiniciada!",
                "¡Partida reiniciada!",
                "Corrida reiniciada!",
                "Забег перезапущен!",
                "Забіг перезапущено!",
                "已重新开始!",
                "",
                "ランを再開始しました!",
                "런이 재시작되었습니다!",
                "Przebieg zrestartowany!",
                "Koşu yeniden başlatıldı!",
            },
            [MsgKey.RestartFailed] = new[]
            {
                "Restart failed, see the log for details.",
                "Échec du redémarrage, consultez le journal pour plus de détails.",
                "Riavvio non riuscito, controlla il log per i dettagli.",
                "Neustart fehlgeschlagen, Details siehe Log.",
                "El reinicio falló, consulta el registro para más detalles.",
                "El reinicio falló, consulta el registro para más detalles.",
                "Falha ao reiniciar, veja o log para mais detalhes.",
                "Не удалось перезапустить, подробности в логе.",
                "Не вдалося перезапустити, деталі в лозі.",
                "重新开始失败,详情请查看日志。",
                "",
                "再開始に失敗しました。詳細はログを確認してください。",
                "재시작에 실패했습니다. 자세한 내용은 로그를 확인하세요.",
                "Restart nie powiódł się, szczegóły w logu.",
                "Yeniden başlatma başarısız oldu, ayrıntılar için günlüğe bakın.",
            },
            [MsgKey.NoSavesSolo] = new[]
            {
                "No solo saves found yet.",
                "Aucune sauvegarde solo trouvée pour l'instant.",
                "Nessun salvataggio solo trovato ancora.",
                "Bisher keine Solo-Speicherstände gefunden.",
                "Aún no se encontraron partidas guardadas en solitario.",
                "Aún no se encontraron partidas guardadas en solitario.",
                "Ainda não há saves solo encontrados.",
                "Соло-сохранения пока не найдены.",
                "Соло-збережень поки не знайдено.",
                "暂未找到单人存档。",
                "",
                "ソロのセーブデータはまだ見つかりません。",
                "아직 솔로 저장 파일이 없습니다.",
                "Nie znaleziono jeszcze żadnych zapisów solo.",
                "Henüz solo kayıt bulunamadı.",
            },
            [MsgKey.NoSavesCoop] = new[]
            {
                "No co-op saves found yet.",
                "Aucune sauvegarde coop trouvée pour l'instant.",
                "Nessun salvataggio co-op trovato ancora.",
                "Bisher keine Koop-Speicherstände gefunden.",
                "Aún no se encontraron partidas guardadas cooperativas.",
                "Aún no se encontraron partidas guardadas cooperativas.",
                "Ainda não há saves cooperativos encontrados.",
                "Кооп-сохранения пока не найдены.",
                "Кооп-збережень поки не знайдено.",
                "暂未找到合作存档。",
                "",
                "協力プレイのセーブデータはまだ見つかりません。",
                "아직 협동 저장 파일이 없습니다.",
                "Nie znaleziono jeszcze żadnych zapisów kooperacji.",
                "Henüz co-op kayıt bulunamadı.",
            },
            [MsgKey.LoadingSavegame] = new[]
            {
                "Loading savegame...",
                "Chargement de la sauvegarde...",
                "Caricamento del salvataggio...",
                "Speicherstand wird geladen...",
                "Cargando partida guardada...",
                "Cargando partida guardada...",
                "Carregando save...",
                "Загрузка сохранения...",
                "Завантаження збереження...",
                "正在加载存档...",
                "",
                "セーブデータを読み込み中...",
                "저장 파일을 불러오는 중...",
                "Wczytywanie zapisu...",
                "Kayıt yükleniyor...",
            },
            [MsgKey.SavegameLoaded] = new[]
            {
                "Save game loaded!",
                "Sauvegarde chargée !",
                "Partita salvata caricata!",
                "Spielstand geladen!",
                "¡Partida guardada cargada!",
                "¡Partida guardada cargada!",
                "Save carregado!",
                "Сохранение загружено!",
                "Збереження завантажено!",
                "存档加载完成!",
                "",
                "セーブデータを読み込みました!",
                "저장 파일을 불러왔습니다!",
                "Zapis wczytany!",
                "Kayıt yüklendi!",
            },
            [MsgKey.TeleportBugHint] = new[]
            {
                "Did something go wrong? If yes, press F1 to open the help screen.",
                "Un problème est survenu ? Si oui, appuyez sur F1 pour ouvrir l'écran d'aide.",
                "Qualcosa è andato storto? Se sì, premi F1 per aprire la schermata di aiuto.",
                "Ist etwas schiefgelaufen? Falls ja, drücke F1, um den Hilfebildschirm zu öffnen.",
                "¿Ha ido algo mal? Si es así, pulsa F1 para abrir la pantalla de ayuda.",
                "¿Salió algo mal? Si es así, presiona F1 para abrir la pantalla de ayuda.",
                "Algo deu errado? Se sim, pressione F1 para abrir a tela de ajuda.",
                "Что-то пошло не так? Если да, нажмите F1, чтобы открыть экран помощи.",
                "Щось пішло не так? Якщо так, натисніть F1, щоб відкрити екран довідки.",
                "出了什么问题吗？如果是的话，按 F1 打开帮助界面。",
                "",
                "何か問題が起きましたか？もしそうなら、F1を押してヘルプ画面を開いてください。",
                "무언가 잘못되었나요? 그렇다면 F1을 눌러 도움말 화면을 열어보세요.",
                "Czy coś poszło nie tak? Jeśli tak, naciśnij F1, aby otworzyć ekran pomocy.",
                "Bir şeyler mi ters gitti? Öyleyse, yardım ekranını açmak için F1 tuşuna basın.",
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(MsgKey key) => LocalizationHelper.Resolve(_table[key]);

        /// <summary>Same as <see cref="Get(MsgKey)"/> with a single format argument (e.g. the ascent number)</summary>
        public static string Get(MsgKey key, object arg0) => string.Format(LocalizationHelper.Resolve(_table[key]), arg0);
    }
}
