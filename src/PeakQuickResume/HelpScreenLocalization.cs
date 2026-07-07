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
    /// Key names (F6/F7/Shift/Alt/Enter), config setting names
    /// (enable-optimized-coop-loading), and "teleportJumpLogic" are left untranslated
    /// everywhere, same as "PEAK Checkpoint Save"/"Quick Resume" - they're literal
    /// identifiers, not prose
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
        OptimizedIntroFormat,  // {0}=load key, {1}=resume key, {2}=optimized value, {3}=base value
        OptimizedSoloNote,
        AskHostFormat,         // {0} = resume key text
        ShiftLineFormat,       // {0}=Shift badge, {1}=resume key badge, {2}=base value
        AltLineFormat,         // {0}=Alt badge, {1}=resume key badge, {2}=alt value
        OptimizedFooterNote,
        DisabledFooterNote,
        DisabledNoteFormat,    // {0} = optimized value
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
            [HelpText.OptimizedIntroFormat] = new[]
            {
                "In COOP, a normal load ({0}, or {1}/Enter with no key held) already uses teleportJumpLogic {2} instead of your usual setting (currently {3}), for exactly the reason above.",
                "En COOP, un chargement normal ({0}, ou {1}/Entrée sans touche maintenue) utilise déjà teleportJumpLogic {2} au lieu de votre réglage habituel (actuellement {3}), pour exactement la raison ci-dessus.",
                "In COOP, un caricamento normale ({0}, oppure {1}/Invio senza tasti premuti) usa già teleportJumpLogic {2} invece della tua impostazione abituale (attualmente {3}), proprio per il motivo sopra.",
                "In COOP verwendet ein normales Laden ({0}, oder {1}/Enter ohne gehaltene Taste) bereits teleportJumpLogic {2} statt deiner üblichen Einstellung (derzeit {3}), genau aus dem oben genannten Grund.",
                "En COOP, una carga normal ({0}, o {1}/Intro sin ninguna tecla pulsada) ya usa teleportJumpLogic {2} en lugar de tu ajuste habitual (actualmente {3}), exactamente por el motivo anterior.",
                "En COOP, una carga normal ({0}, o {1}/Enter sin ninguna tecla presionada) ya usa teleportJumpLogic {2} en lugar de tu ajuste habitual (actualmente {3}), exactamente por el motivo anterior.",
                "No COOP, um carregamento normal ({0}, ou {1}/Enter sem nenhuma tecla pressionada) já usa teleportJumpLogic {2} em vez da sua configuração habitual (atualmente {3}), exatamente pelo motivo acima.",
                "В КООПЕ обычная загрузка ({0}, или {1}/Enter без зажатой клавиши) уже использует teleportJumpLogic {2} вместо вашей обычной настройки (сейчас {3}) именно по указанной выше причине.",
                "У КООПІ звичайне завантаження ({0}, або {1}/Enter без затиснутої клавіші) вже використовує teleportJumpLogic {2} замість вашого звичного налаштування (наразі {3}) саме з вищезазначеної причини.",
                "在合作模式下，普通加载（{0}，或不按任何键的 {1}/回车）已经使用 teleportJumpLogic {2}，而不是你平时的设置（当前为 {3}），正是出于上面提到的原因。",
                "",
                "COOPでは、通常のロード（{0}、またはキーを押さない{1}/Enter）は、上記の理由からすでにteleportJumpLogic {2}を使用しており、あなたの普段の設定（現在{3}）は使われません。",
                "협동 모드에서는 일반 로드({0}, 또는 아무 키도 누르지 않은 {1}/Enter)가 위에서 설명한 이유로 이미 평소 설정(현재 {3}) 대신 teleportJumpLogic {2}를 사용합니다.",
                "W trybie KOOP zwykłe wczytywanie ({0}, lub {1}/Enter bez trzymanego klawisza) już używa teleportJumpLogic {2} zamiast twojego zwykłego ustawienia (obecnie {3}), właśnie z powodu wyjaśnionego powyżej.",
                "KOOP modunda, düz bir yükleme ({0} veya hiçbir tuşa basılmadan {1}/Enter) tam da yukarıda açıklanan nedenden dolayı zaten normal ayarın (şu anda {3}) yerine teleportJumpLogic {2} kullanır.",
            },
            [HelpText.OptimizedSoloNote] = new[]
            {
                "Solo isn't affected and always uses your usual setting.",
                "Le solo n'est pas concerné et utilise toujours votre réglage habituel.",
                "La modalità solo non è interessata e usa sempre la tua impostazione abituale.",
                "Solo ist davon nicht betroffen und verwendet immer deine übliche Einstellung.",
                "El modo solo no se ve afectado y siempre usa tu ajuste habitual.",
                "El modo solo no se ve afectado y siempre usa tu ajuste habitual.",
                "O modo solo não é afetado e sempre usa sua configuração habitual.",
                "Одиночный режим не затрагивается и всегда использует вашу обычную настройку.",
                "Одиночний режим не зачіпається і завжди використовує ваше звичне налаштування.",
                "单人模式不受影响，始终使用你平时的设置。",
                "",
                "ソロプレイには影響せず、常にあなたの普段の設定が使用されます。",
                "솔로 플레이는 영향을 받지 않으며 항상 평소 설정을 사용합니다.",
                "Tryb solo nie jest tym objęty i zawsze używa twojego zwykłego ustawienia.",
                "Solo modu bundan etkilenmez ve her zaman normal ayarını kullanır.",
            },
            [HelpText.AskHostFormat] = new[]
            {
                "Still stuck after restarting? Ask your HOST to reload the SAME save from the picker ({0}) while holding:",
                "Toujours bloqué après avoir redémarré ? Demandez à votre HÔTE de recharger la MÊME sauvegarde depuis le sélecteur ({0}) en maintenant :",
                "Ancora bloccato dopo il riavvio? Chiedi al tuo HOST di ricaricare LO STESSO salvataggio dal selettore ({0}) tenendo premuto:",
                "Immer noch festgefahren nach dem Neustart? Bitte deinen HOST, denselben Speicherstand aus der Auswahl ({0}) neu zu laden, und dabei zu halten:",
                "¿Sigues atascado después de reiniciar? Pide a tu HOST que recargue la MISMA partida desde el selector ({0}) manteniendo pulsado:",
                "¿Sigues atascado después de reiniciar? Pide a tu HOST que recargue la MISMA partida desde el selector ({0}) manteniendo presionado:",
                "Ainda travado depois de reiniciar? Peça ao seu HOST para recarregar o MESMO save pelo seletor ({0}) segurando:",
                "Всё ещё не помогло после перезапуска? Попросите вашего ХОСТА перезагрузить ТО ЖЕ сохранение из выбора ({0}), удерживая:",
                "Все ще не допомогло після перезапуску? Попросіть вашого ХОСТА перезавантажити ТЕ САМЕ збереження з вибору ({0}), утримуючи:",
                "重启后仍未解决？请让你的房主在存档选择器（{0}）中按住以下按键重新加载同一个存档：",
                "",
                "再起動しても直らない場合は、ホストに、セーブ選択画面（{0}）で以下のキーを押しながら同じセーブをリロードしてもらってください：",
                "재시작 후에도 여전히 문제가 있나요? 호스트에게 저장 파일 선택 화면({0})에서 다음 키를 누른 채로 같은 저장 파일을 다시 불러오도록 요청하세요:",
                "Nadal utknąłeś po restarcie? Poproś swojego HOSTA, aby ponownie wczytał TEN SAM zapis z wyboru ({0}), trzymając:",
                "Yeniden başlattıktan sonra hâlâ takılıyor musun? SUNUCUNDAN, kayıt seçiciden ({0}) şunu basılı tutarak AYNI kaydı yeniden yüklemesini isteyin:",
            },
            [HelpText.ShiftLineFormat] = new[]
            {
                "{0} + {1}/Enter => your usual setting ({2})",
                "{0} + {1}/Entrée => votre réglage habituel ({2})",
                "{0} + {1}/Invio => la tua impostazione abituale ({2})",
                "{0} + {1}/Enter => deine übliche Einstellung ({2})",
                "{0} + {1}/Intro => tu ajuste habitual ({2})",
                "{0} + {1}/Enter => tu ajuste habitual ({2})",
                "{0} + {1}/Enter => sua configuração habitual ({2})",
                "{0} + {1}/Enter => ваша обычная настройка ({2})",
                "{0} + {1}/Enter => ваше звичне налаштування ({2})",
                "{0} + {1}/回车 => 你平时的设置 ({2})",
                "",
                "{0} + {1}/Enter => あなたの普段の設定（{2}）",
                "{0} + {1}/Enter => 평소 설정 ({2})",
                "{0} + {1}/Enter => twoje zwykłe ustawienie ({2})",
                "{0} + {1}/Enter => normal ayarın ({2})",
            },
            [HelpText.AltLineFormat] = new[]
            {
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Entrée => teleportJumpLogic {2}",
                "{0} + {1}/Invio => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Intro => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/回车 => teleportJumpLogic {2}",
                "",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
                "{0} + {1}/Enter => teleportJumpLogic {2}",
            },
            [HelpText.OptimizedFooterNote] = new[]
            {
                "If it still happens, try Shift or Alt instead. It only affects the next load, then reverts on its own.",
                "Si cela se produit encore, essayez plutôt Maj ou Alt. Cela n'affecte que le prochain chargement, puis revient à la normale tout seul.",
                "Se succede ancora, prova invece Maiusc o Alt. Influisce solo sul prossimo caricamento, poi torna alla normalità da solo.",
                "Falls es weiterhin passiert, versuche stattdessen Umschalt oder Alt. Das wirkt sich nur auf das nächste Laden aus und setzt sich danach von selbst zurück.",
                "Si sigue ocurriendo, prueba con Mayús o Alt en su lugar. Solo afecta a la próxima carga y luego vuelve a la normalidad por sí solo.",
                "Si sigue ocurriendo, prueba con Shift o Alt en su lugar. Solo afecta a la próxima carga y luego vuelve a la normalidad por sí solo.",
                "Se ainda acontecer, tente Shift ou Alt em vez disso. Isso afeta apenas o próximo carregamento e depois volta ao normal sozinho.",
                "Если это всё ещё происходит, попробуйте вместо этого Shift или Alt. Это влияет только на следующую загрузку, а затем само возвращается к норме.",
                "Якщо це все ще трапляється, спробуйте натомість Shift або Alt. Це впливає лише на наступне завантаження, а потім само повертається до норми.",
                "如果问题仍然出现，可以改为尝试 Shift 或 Alt。这只影响下一次加载，之后会自动恢复。",
                "",
                "それでも起きる場合は、代わりにShiftまたはAltを試してください。これは次の1回のロードにのみ影響し、その後自動的に元に戻ります。",
                "그래도 계속 발생한다면 대신 Shift나 Alt를 시도해 보세요. 이는 다음 로드에만 영향을 주며, 이후 자동으로 원래대로 돌아갑니다.",
                "Jeśli to nadal się zdarza, spróbuj zamiast tego Shift lub Alt. Wpływa to tylko na następne wczytanie, a potem samo się cofa.",
                "Hâlâ oluyorsa, bunun yerine Shift veya Alt tuşunu deneyin. Bu yalnızca bir sonraki yüklemeyi etkiler, ardından kendiliğinden eski haline döner.",
            },
            [HelpText.DisabledFooterNote] = new[]
            {
                "If one doesn't help, try the other. It only affects the next load, then reverts on its own.",
                "Si l'un ne fonctionne pas, essayez l'autre. Cela n'affecte que le prochain chargement, puis revient à la normale tout seul.",
                "Se uno non aiuta, prova l'altro. Influisce solo sul prossimo caricamento, poi torna alla normalità da solo.",
                "Wenn eines nicht hilft, versuche das andere. Das wirkt sich nur auf das nächste Laden aus und setzt sich danach von selbst zurück.",
                "Si uno no funciona, prueba el otro. Solo afecta a la próxima carga y luego vuelve a la normalidad por sí solo.",
                "Si uno no funciona, prueba el otro. Solo afecta a la próxima carga y luego vuelve a la normalidad por sí solo.",
                "Se um não ajudar, tente o outro. Isso afeta apenas o próximo carregamento e depois volta ao normal sozinho.",
                "Если один не помогает, попробуйте другой. Это влияет только на следующую загрузку, а затем само возвращается к норме.",
                "Якщо один не допомагає, спробуйте інший. Це впливає лише на наступне завантаження, а потім само повертається до норми.",
                "如果一个不管用，可以试试另一个。这只影响下一次加载，之后会自动恢复。",
                "",
                "片方で効果がなければ、もう片方を試してください。これは次の1回のロードにのみ影響し、その後自動的に元に戻ります。",
                "하나가 효과가 없다면 다른 것을 시도해 보세요. 이는 다음 로드에만 영향을 주며, 이후 자동으로 원래대로 돌아갑니다.",
                "Jeśli jedno nie pomoże, spróbuj drugiego. Wpływa to tylko na następne wczytanie, a potem samo się cofa.",
                "Biri işe yaramazsa diğerini deneyin. Bu yalnızca bir sonraki yüklemeyi etkiler, ardından kendiliğinden eski haline döner.",
            },
            [HelpText.DisabledNoteFormat] = new[]
            {
                "COOP auto-optimization is currently OFF (enable-optimized-coop-loading in the config). Turning it on makes a normal COOP load use teleportJumpLogic {0} by default, which testing found avoids most of the issues above.",
                "L'auto-optimisation COOP est actuellement DÉSACTIVÉE (enable-optimized-coop-loading dans la config). L'activer fait qu'un chargement COOP normal utilise par défaut teleportJumpLogic {0}, ce qui, d'après les tests, évite la plupart des problèmes ci-dessus.",
                "L'auto-ottimizzazione COOP è attualmente DISATTIVATA (enable-optimized-coop-loading nella config). Attivandola, un caricamento COOP normale userà come predefinito teleportJumpLogic {0}, il che, secondo i test, evita la maggior parte dei problemi sopra.",
                "Die COOP-Auto-Optimierung ist derzeit AUS (enable-optimized-coop-loading in der Konfiguration). Wird sie aktiviert, verwendet ein normales COOP-Laden standardmäßig teleportJumpLogic {0}, was laut Tests die meisten der oben genannten Probleme vermeidet.",
                "La auto-optimización de COOP está actualmente DESACTIVADA (enable-optimized-coop-loading en la configuración). Activarla hace que una carga COOP normal use por defecto teleportJumpLogic {0}, lo que, según las pruebas, evita la mayoría de los problemas mencionados arriba.",
                "La auto-optimización de COOP está actualmente DESACTIVADA (enable-optimized-coop-loading en la configuración). Activarla hace que una carga COOP normal use por defecto teleportJumpLogic {0}, lo que, según las pruebas, evita la mayoría de los problemas mencionados arriba.",
                "A auto-otimização do COOP está atualmente DESATIVADA (enable-optimized-coop-loading na configuração). Ativá-la faz com que um carregamento COOP normal use por padrão teleportJumpLogic {0}, o que, segundo os testes, evita a maioria dos problemas acima.",
                "Автооптимизация КООПА сейчас ВЫКЛЮЧЕНА (enable-optimized-coop-loading в конфиге). Включив её, обычная загрузка в КООПЕ будет по умолчанию использовать teleportJumpLogic {0}, что, по результатам тестов, позволяет избежать большинства проблем, указанных выше.",
                "Автооптимізація КООПУ зараз ВИМКНЕНА (enable-optimized-coop-loading у конфізі). Увімкнувши її, звичайне завантаження в КООПІ використовуватиме за замовчуванням teleportJumpLogic {0}, що, за результатами тестів, дає змогу уникнути більшості проблем, зазначених вище.",
                "合作模式自动优化目前处于关闭状态（配置中的 enable-optimized-coop-loading）。开启后，普通的合作模式加载将默认使用 teleportJumpLogic {0}，测试表明这能避免上述大多数问题。",
                "",
                "COOP自動最適化は現在OFFです（設定のenable-optimized-coop-loading）。有効にすると、通常のCOOPロードはデフォルトでteleportJumpLogic {0}を使用するようになり、テストの結果、上記の問題のほとんどを回避できることが分かっています。",
                "협동 자동 최적화가 현재 꺼져 있습니다 (설정의 enable-optimized-coop-loading). 이를 켜면 일반 협동 로드가 기본적으로 teleportJumpLogic {0}을(를) 사용하게 되며, 테스트 결과 위에 나열된 대부분의 문제를 피할 수 있는 것으로 확인되었습니다.",
                "Automatyczna optymalizacja KOOP jest obecnie WYŁĄCZONA (enable-optimized-coop-loading w konfiguracji). Włączenie jej sprawia, że zwykłe wczytywanie KOOP domyślnie używa teleportJumpLogic {0}, co według testów pozwala uniknąć większości problemów wymienionych powyżej.",
                "KOOP otomatik optimizasyonu şu anda KAPALI (yapılandırmada enable-optimized-coop-loading). Açmak, düz bir KOOP yüklemesinin varsayılan olarak teleportJumpLogic {0} kullanmasını sağlar; testler bunun yukarıda listelenen sorunların çoğunu önlediğini gösterdi.",
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
