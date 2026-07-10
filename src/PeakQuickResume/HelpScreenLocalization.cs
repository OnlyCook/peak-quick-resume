using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Translations for the F1 help screen's body text (<see cref="HelpScreenContent"/>).
    /// Same rules as <see cref="SavePickerLocalization"/>/<see cref="PauseMenuLocalization"/>:
    /// indexed by <c>(int)LocalizedText.Language</c>, covering every language the game
    /// itself ships; "Traditional Chinese" is left blank (not one of the game's shipped
    /// languages) and falls back to English
    ///
    /// Key names (F6/F7/Enter) are left untranslated everywhere, same as
    /// "PEAK Checkpoint Save"/"Quick Resume" - they're literal identifiers, not prose
    /// </summary>
    internal enum HelpText
    {
        HelpTitleWord,         // combined with the untranslated "Quick Resume" as "Quick Resume {0}"
        Close,
        Intro1,
        NativeLoadFormat,      // {0} = load key badge
        QuickResumeFormat,     // {0} = resume key badge (used twice in the sentence)
        BugTitle,
        BugSymptoms,
        BugExplain,
        RestartFirstTitle,
        RestartFirstNote,
        AchievementsNote,
    }

    internal static class HelpScreenLocalization
    {
        // Array order MUST match LocalizedText.Language's declaration order:
        // English, French, Italian, German, SpanishSpain, SpanishLatam, BRPortuguese,
        // Russian, Ukrainian, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Polish, Turkish
        private static readonly Dictionary<HelpText, string[]> _table = new Dictionary<HelpText, string[]>
        {
            [HelpText.HelpTitleWord] = new[]
            {
                "Help", "Aide", "Aiuto", "Hilfe", "Ayuda", "Ayuda", "Ajuda",
                "Справка", "Довідка", "帮助", "", "ヘルプ", "도움말", "Pomoc", "Yardım",
            },
            [HelpText.Close] = new[]
            {
                "Close", "Fermer", "Chiudi", "Schließen", "Cerrar", "Cerrar", "Fechar",
                "Закрыть", "Закрити", "关闭", "", "閉じる", "닫기", "Zamknij", "Kapat",
            },
            [HelpText.Intro1] = new[]
            {
                "Progress saves at every campfire you light (host-only, the host must reload each session).",
                "La progression est sauvegardée à chaque feu de camp que vous allumez (hôte uniquement, l'hôte doit recharger à chaque session).",
                "I progressi vengono salvati a ogni falò che accendi (solo host, l'host deve ricaricare a ogni sessione).",
                "Der Fortschritt wird an jedem Lagerfeuer gespeichert, das du entzündest (nur Host, der Host muss die Sitzung jedes Mal neu laden).",
                "El progreso se guarda en cada hoguera que enciendes (solo el host, que debe recargar cada sesión).",
                "El progreso se guarda en cada fogata que enciendes (solo el host, que debe recargar cada sesión).",
                "O progresso é salvo em cada fogueira que você acende (somente o host, que precisa recarregar a cada sessão).",
                "Прогресс сохраняется у каждого зажжённого костра (только у хоста, хосту нужно перезагружать каждую сессию).",
                "Прогрес зберігається біля кожного запаленого вогнища (лише в хоста, хосту потрібно перезавантажувати щосесії).",
                "在你点燃的每个篝火处保存进度（仅限房主，房主每次都需要重新加载）。",
                "",
                "焚き火を灯すたびに進行状況が保存されます（ホストのみ。ホストはセッションごとに再読み込みが必要です）。",
                "모닥불을 피울 때마다 진행 상황이 저장됩니다 (호스트 전용, 호스트는 매 세션마다 다시 불러와야 합니다).",
                "Postęp zapisuje się przy każdym rozpalonym ognisku (tylko host, host musi wczytywać ponownie co sesję).",
                "İlerleme yaktığın her kamp ateşinde kaydedilir (yalnızca sunucu; sunucu her oturumda yeniden yüklemelidir).",
            },
            [HelpText.NativeLoadFormat] = new[]
            {
                "Native load: start a level and press {0}.",
                "Chargement natif : démarrez un niveau et appuyez sur {0}.",
                "Caricamento nativo: avvia un livello e premi {0}.",
                "Natives Laden: Starte ein Level und drücke {0}.",
                "Carga nativa: inicia un nivel y pulsa {0}.",
                "Carga nativa: inicia un nivel y presiona {0}.",
                "Carregamento nativo: inicie um nível e pressione {0}.",
                "Родная загрузка: начните уровень и нажмите {0}.",
                "Рідне завантаження: почніть рівень і натисніть {0}.",
                "原生加载：进入关卡后按下 {0}。",
                "",
                "ネイティブロード：レベルを開始して {0} を押します。",
                "네이티브 로드: 레벨을 시작한 뒤 {0}을(를) 누르세요.",
                "Natywne wczytywanie: rozpocznij poziom i naciśnij {0}.",
                "Yerel yükleme: bir bölüme başlayın ve {0} tuşuna basın.",
            },
            [HelpText.QuickResumeFormat] = new[]
            {
                "Quick Resume: press {0} ANYWHERE to open the save picker, arrow keys to choose, {0}/Enter to load (press it twice for your latest).",
                "Quick Resume : appuyez sur {0} N'IMPORTE OÙ pour ouvrir le sélecteur de sauvegardes, les flèches pour choisir, {0}/Entrée pour charger (appuyez-y deux fois pour la plus récente).",
                "Quick Resume: premi {0} OVUNQUE per aprire il selettore dei salvataggi, le frecce per scegliere, {0}/Invio per caricare (premilo due volte per il più recente).",
                "Quick Resume: Drücke {0} ÜBERALL, um die Speicherstandauswahl zu öffnen, Pfeiltasten zum Auswählen, {0}/Enter zum Laden (zweimal drücken für den neuesten).",
                "Quick Resume: pulsa {0} EN CUALQUIER LUGAR para abrir el selector de partidas, flechas para elegir, {0}/Intro para cargar (púlsalo dos veces para la más reciente).",
                "Quick Resume: presiona {0} EN CUALQUIER LUGAR para abrir el selector de partidas, flechas para elegir, {0}/Enter para cargar (presiónalo dos veces para la más reciente).",
                "Quick Resume: pressione {0} EM QUALQUER LUGAR para abrir o seletor de saves, setas para escolher, {0}/Enter para carregar (pressione duas vezes para o mais recente).",
                "Quick Resume: нажмите {0} В ЛЮБОМ МЕСТЕ, чтобы открыть выбор сохранений, стрелки для выбора, {0}/Enter для загрузки (нажмите дважды для последнего).",
                "Quick Resume: натисніть {0} БУДЬ-ДЕ, щоб відкрити вибір збережень, стрілки для вибору, {0}/Enter для завантаження (натисніть двічі для останнього).",
                "Quick Resume：在任何地方按下 {0} 打开存档选择器，方向键选择，{0}/回车加载（连按两次加载最新存档）。",
                "",
                "Quick Resume：どこでも {0} を押すとセーブ選択画面が開きます。矢印キーで選択し、{0}/Enterでロード（2回押すと最新のセーブをロード）。",
                "Quick Resume: 어디서든 {0}을(를) 눌러 저장 파일 선택 화면을 열고, 방향키로 선택한 뒤 {0}/Enter로 불러오세요 (두 번 누르면 최신 저장 파일을 불러옵니다).",
                "Quick Resume: naciśnij {0} GDZIEKOLWIEK, aby otworzyć wybór zapisów, strzałki do wyboru, {0}/Enter do wczytania (naciśnij dwukrotnie, aby wczytać najnowszy).",
                "Quick Resume: kayıt seçiciyi açmak için HER YERDE {0} tuşuna basın, seçim için ok tuşları, yüklemek için {0}/Enter (en sonuncusu için iki kez basın).",
            },
            [HelpText.BugTitle] = new[]
            {
                "Did loading a save go wrong?",
                "Le chargement d'une sauvegarde a-t-il posé problème ?",
                "Il caricamento di un salvataggio ha causato un problema?",
                "Ist beim Laden eines Speicherstands etwas schiefgelaufen?",
                "¿Algo salió mal al cargar una partida?",
                "¿Algo salió mal al cargar una partida?",
                "Algo deu errado ao carregar um save?",
                "Что-то пошло не так при загрузке сохранения?",
                "Щось пішло не так під час завантаження збереження?",
                "加载存档出问题了吗？",
                "",
                "セーブのロードで問題が起きましたか？",
                "저장 파일을 불러올 때 문제가 발생했나요?",
                "Czy podczas wczytywania zapisu coś poszło nie tak?",
                "Bir kaydı yüklerken bir şeyler mi ters gitti?",
            },
            [HelpText.BugSymptoms] = new[]
            {
                "(blank/dark map, bouncing up and down, falling through the floor, or you never actually reached your campfire)",
                "(carte vide/sombre, rebondissement de haut en bas, chute à travers le sol, ou vous n'avez en fait jamais atteint votre feu de camp)",
                "(mappa vuota/scura, rimbalzo su e giù, caduta attraverso il pavimento, oppure non hai mai effettivamente raggiunto il tuo falò)",
                "(leere/dunkle Karte, Auf-und-ab-Springen, Durchfallen durch den Boden, oder du hast dein Lagerfeuer eigentlich nie erreicht)",
                "(mapa vacío/oscuro, rebotando arriba y abajo, cayendo a través del suelo, o en realidad nunca llegaste a tu hoguera)",
                "(mapa vacío/oscuro, rebotando arriba y abajo, cayendo a través del suelo, o en realidad nunca llegaste a tu fogata)",
                "(mapa vazio/escuro, quicando para cima e para baixo, caindo através do chão, ou você nunca chegou realmente à sua fogueira)",
                "(пустая/тёмная карта, подпрыгивание вверх-вниз, падение сквозь пол, или вы так и не добрались до костра)",
                "(порожня/темна карта, підстрибування вгору-вниз, падіння крізь підлогу, або ви так і не дісталися до вогнища)",
                "（地图空白/漆黑，上下弹跳，穿过地板掉落，或者其实根本没有到达篝火旁）",
                "",
                "（マップが空/暗い、上下に跳ね続ける、床をすり抜けて落下する、または実際には焚き火にたどり着いていない）",
                "(빈/어두운 맵, 위아래로 튕김, 바닥을 뚫고 떨어짐, 또는 실제로 모닥불에 도착하지 않음)",
                "(pusta/ciemna mapa, odbijanie się góra-dół, przepadanie przez podłogę, albo w ogóle nie dotarłeś do ogniska)",
                "(boş/karanlık harita, yukarı aşağı zıplama, zeminin içinden düşme, veya aslında hiç kamp ateşine ulaşmamış olma)",
            },
            [HelpText.BugExplain] = new[]
            {
                "This is a rare bug in the checkpoint mod's own teleport, mostly affecting clients in multiplayer. Quick Resume doesn't cause it and can't fix it directly.",
                "C'est un bug rare dans le téléport du mod de sauvegarde lui-même, touchant surtout les clients en multijoueur. Quick Resume ne le cause pas et ne peut pas le corriger directement.",
                "È un bug raro nel teletrasporto della mod di salvataggio stessa, che colpisce soprattutto i client in multiplayer. Quick Resume non lo causa e non può correggerlo direttamente.",
                "Das ist ein seltener Fehler im Teleport des Speicher-Mods selbst, der hauptsächlich Clients im Mehrspielermodus betrifft. Quick Resume verursacht ihn nicht und kann ihn nicht direkt beheben.",
                "Es un fallo poco frecuente en el propio teletransporte del mod de guardado, que afecta sobre todo a los clientes en multijugador. Quick Resume no lo causa ni puede corregirlo directamente.",
                "Es un fallo poco frecuente en el propio teletransporte del mod de guardado, que afecta sobre todo a los clientes en multijugador. Quick Resume no lo causa ni puede corregirlo directamente.",
                "Esse é um bug raro no próprio teletransporte do mod de save, que afeta principalmente os clientes no multiplayer. O Quick Resume não causa isso e não pode corrigir diretamente.",
                "Это редкий баг в собственной телепортации мода сохранений, который в основном затрагивает клиентов в мультиплеере. Quick Resume не вызывает его и не может исправить напрямую.",
                "Це рідкісний баг у власній телепортації мода збережень, який здебільшого зачіпає клієнтів у мультиплеєрі. Quick Resume не спричиняє це і не може виправити напряму.",
                "这是存档模组自身传送逻辑中一个罕见的漏洞，主要影响多人游戏中的客户端。这不是 Quick Resume 造成的问题，也不是它能直接修复的问题。",
                "",
                "これはセーブMod自体のテレポート処理で稀に起きるバグで、主にマルチプレイのクライアント側に影響します。Quick Resumeが原因ではなく、直接修正することもできません。",
                "이것은 저장 모드 자체의 텔레포트에서 드물게 발생하는 버그로, 주로 멀티플레이어의 클라이언트에게 영향을 미칩니다. Quick Resume이 원인이 아니며 직접 고칠 수도 없습니다.",
                "To rzadki błąd w samej teleportacji moda zapisu, dotykający głównie klientów w trybie wieloosobowym. Quick Resume go nie powoduje i nie może go bezpośrednio naprawić.",
                "Bu, kayıt modunun kendi ışınlanma sisteminde nadiren yaşanan bir hatadır ve çoğunlukla çok oyunculu modda istemcileri etkiler. Quick Resume buna neden olmaz ve doğrudan düzeltemez.",
            },
            [HelpText.RestartFirstTitle] = new[]
            {
                "Try this first:",
                "Essayez d'abord ceci :",
                "Prova prima questo:",
                "Versuche das zuerst:",
                "Prueba esto primero:",
                "Prueba esto primero:",
                "Tente isto primeiro:",
                "Сначала попробуйте это:",
                "Спершу спробуйте це:",
                "先试试这个：",
                "",
                "まずこれを試してください：",
                "먼저 이것을 시도해 보세요:",
                "Najpierw spróbuj tego:",
                "Önce şunu deneyin:",
            },
            [HelpText.RestartFirstNote] = new[]
            {
                "have everyone quit and rejoin (or fully restart) the game, then reload the same save. This alone fixes most cases, so try it before anything below.",
                "que tout le monde quitte puis rejoigne (ou redémarre complètement) le jeu, puis rechargez la même sauvegarde. Cela suffit à résoudre la plupart des cas, essayez-le donc avant tout ce qui suit.",
                "fai in modo che tutti escano e rientrino (o riavviino completamente) il gioco, poi ricarica lo stesso salvataggio. Questo da solo risolve la maggior parte dei casi, quindi provalo prima di tutto il resto.",
                "lasst alle das Spiel verlassen und wieder beitreten (oder komplett neu starten), und ladet dann denselben Speicherstand erneut. Das allein löst die meisten Fälle, versucht es also, bevor ihr etwas anderes unten ausprobiert.",
                "que todos salgan y vuelvan a entrar (o reinicien completamente) el juego, y luego recarguen la misma partida. Esto por sí solo soluciona la mayoría de los casos, así que pruébalo antes que cualquier cosa de abajo.",
                "que todos salgan y vuelvan a entrar (o reinicien completamente) el juego, y luego recarguen la misma partida. Esto por sí solo soluciona la mayoría de los casos, así que pruébalo antes que cualquier otra cosa de abajo.",
                "que todos saiam e entrem novamente (ou reiniciem completamente) o jogo, depois recarreguem o mesmo save. Isso sozinho já resolve a maioria dos casos, então tente antes de qualquer coisa abaixo.",
                "пусть все выйдут и снова зайдут в игру (или полностью перезапустят её), а затем загрузите то же сохранение. Одно это решает большинство случаев, так что попробуйте это, прежде чем пробовать что-либо ниже.",
                "нехай усі вийдуть і знову зайдуть у гру (або повністю перезапустять її), а потім завантажте те саме збереження. Це саме по собі вирішує більшість випадків, тож спробуйте це перед усім іншим нижче.",
                "让所有人退出并重新加入游戏（或彻底重启游戏），然后再加载同一个存档。这一步就能解决大多数情况，所以请先试试这个，再尝试下面的方法。",
                "",
                "全員が一度ゲームを退出して再参加する（またはゲームを完全に再起動する）、その後同じセーブをロードし直してください。これだけでほとんどのケースが解決するので、下記を試す前にまずこれを行ってください。",
                "모두가 게임을 나갔다가 다시 참가하거나(또는 게임을 완전히 재시작) 한 뒤, 같은 저장 파일을 다시 불러오세요. 이것만으로 대부분의 경우가 해결되니, 아래의 다른 방법을 시도하기 전에 먼저 이것을 시도하세요.",
                "niech wszyscy opuszczą grę i dołączą ponownie (albo całkowicie ją zrestartują), a następnie wczytajcie ten sam zapis. Samo to rozwiązuje większość przypadków, więc wypróbuj to przed czymkolwiek poniżej.",
                "herkesin oyundan çıkıp yeniden katılmasını (veya oyunu tamamen yeniden başlatmasını) sağlayın, ardından aynı kaydı tekrar yükleyin. Bu tek başına çoğu durumu çözer, bu yüzden aşağıdaki diğer şeyleri denemeden önce bunu deneyin.",
            },
            [HelpText.AchievementsNote] = new[]
            {
                "Loading a save may unlock Steam achievements. Skip it if you want to earn everything yourself.",
                "Charger une sauvegarde peut débloquer des succès Steam. Évitez-le si vous voulez tout obtenir par vous-même.",
                "Caricare un salvataggio potrebbe sbloccare obiettivi Steam. Evitalo se vuoi ottenere tutto da solo.",
                "Das Laden eines Speicherstands kann Steam-Erfolge freischalten. Verzichte darauf, wenn du alles selbst erspielen möchtest.",
                "Cargar una partida puede desbloquear logros de Steam. Evítalo si quieres conseguirlo todo por ti mismo.",
                "Cargar una partida puede desbloquear logros de Steam. Evítalo si quieres conseguirlo todo por ti mismo.",
                "Carregar um save pode desbloquear conquistas da Steam. Evite isso se quiser conquistar tudo por conta própria.",
                "Загрузка сохранения может разблокировать достижения Steam. Пропустите это, если хотите получить всё сами.",
                "Завантаження збереження може розблокувати досягнення Steam. Пропустіть це, якщо хочете отримати все самостійно.",
                "加载存档可能会解锁 Steam 成就。如果你想靠自己获得所有成就，请跳过此功能。",
                "",
                "セーブをロードするとSteam実績が解除されることがあります。自分の力だけで実績をすべて獲得したい場合は使用しないでください。",
                "저장 파일을 불러오면 Steam 업적이 잠금 해제될 수 있습니다. 스스로 모든 업적을 획득하고 싶다면 사용하지 마세요.",
                "Wczytanie zapisu może odblokować osiągnięcia Steam. Pomiń to, jeśli chcesz zdobyć wszystko samodzielnie.",
                "Bir kaydı yüklemek Steam başarımlarının kilidini açabilir. Her şeyi kendi başına kazanmak istiyorsan bunu atla.",
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(HelpText key) => LocalizationHelper.Resolve(_table[key]);

        /// <summary>Same as <see cref="Get(HelpText)"/>, then <see cref="string.Format(string, object[])"/>'d against <paramref name="args"/></summary>
        public static string Get(HelpText key, params object[] args) => string.Format(Get(key), args);
    }
}
