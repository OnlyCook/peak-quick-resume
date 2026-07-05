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
                "Did loading a save bug you out?",
                "Le chargement d'une sauvegarde vous a-t-il fait bugger ?",
                "Il caricamento di un salvataggio ti ha causato un bug?",
                "Hat dich das Laden eines Speicherstands verbuggt?",
                "¿Cargar una partida te ha dejado bugueado?",
                "¿Cargar una partida te dejó bugueado?",
                "Carregar um save te deixou bugado?",
                "Загрузка сохранения привела к багу?",
                "Завантаження збереження призвело до багу?",
                "加载存档时出现异常了吗？",
                "",
                "セーブのロードで不具合が起きましたか？",
                "저장 파일을 불러왔을 때 버그가 발생했나요?",
                "Czy wczytanie zapisu spowodowało u ciebie błąd?",
                "Bir kaydı yüklemek seni bugla mı bıraktı?",
            },
            [HelpText.BugSymptoms] = new[]
            {
                "(empty/dark map, stuck glitching up and down, falling through the world, or you never actually moved to your campfire)",
                "(carte vide/sombre, bloqué à glitcher de haut en bas, chute à travers le monde, ou vous n'avez en fait jamais été déplacé jusqu'à votre feu de camp)",
                "(mappa vuota/scura, bloccato a glitchare su e giù, caduta attraverso il mondo, oppure non sei mai stato effettivamente spostato al tuo falò)",
                "(leere/dunkle Karte, festhängendes Auf-und-ab-Geglitsche, Durchfallen durch die Welt, oder du wurdest tatsächlich nie zu deinem Lagerfeuer bewegt)",
                "(mapa vacío/oscuro, atascado bugueando arriba y abajo, cayendo a través del mundo, o en realidad nunca te movieron a tu hoguera)",
                "(mapa vacío/oscuro, atascado bugueando arriba y abajo, cayendo a través del mundo, o en realidad nunca te movieron a tu fogata)",
                "(mapa vazio/escuro, preso bugando para cima e para baixo, caindo através do mundo, ou você nunca foi realmente movido até sua fogueira)",
                "(пустая/тёмная карта, застревание в глюке вверх-вниз, падение сквозь мир, или вас на самом деле так и не переместило к костру)",
                "(порожня/темна карта, застрягання в глюку вгору-вниз, падіння крізь світ, або вас насправді так і не перемістило до вогнища)",
                "（地图空白/漆黑，卡在上下抖动的故障中，穿模掉出世界，或者其实根本没有被传送到篝火旁）",
                "",
                "（マップが空/暗い、上下にカクカク動き続ける、ワールドをすり抜けて落下する、または実際には焚き火の場所へ移動していない）",
                "(빈/어두운 맵, 위아래로 계속 튕기는 버그, 월드를 뚫고 떨어짐, 또는 실제로 모닥불 위치로 이동하지 않음)",
                "(pusta/ciemna mapa, utknięcie w błędzie góra-dół, przepadanie przez świat, albo w ogóle nie zostałeś przeniesiony do ogniska)",
                "(boş/karanlık harita, yukarı aşağı takılıp bug yapma, dünyanın içinden düşme veya aslında hiç kamp ateşine taşınmamış olma)",
            },
            [HelpText.BugExplain] = new[]
            {
                "This is an occasional hiccup in PEAK Checkpoint Save's own teleport, mostly hitting clients in multiplayer, not something Quick Resume causes or can fix directly.",
                "C'est un incident occasionnel dans le téléport de PEAK Checkpoint Save lui-même, touchant surtout les clients en multijoueur, pas quelque chose que Quick Resume provoque ou peut corriger directement.",
                "Si tratta di un occasionale intoppo nel teletrasporto di PEAK Checkpoint Save stesso, che colpisce soprattutto i client in multiplayer, non qualcosa che Quick Resume causa o può correggere direttamente.",
                "Das ist ein gelegentlicher Aussetzer in PEAK Checkpoint Saves eigenem Teleport, der hauptsächlich Clients im Mehrspielermodus betrifft - nichts, was Quick Resume verursacht oder direkt beheben kann.",
                "Este es un fallo ocasional en el propio teletransporte de PEAK Checkpoint Save, que afecta sobre todo a los clientes en multijugador; no es algo que Quick Resume cause ni pueda corregir directamente.",
                "Este es un fallo ocasional en el propio teletransporte de PEAK Checkpoint Save, que afecta sobre todo a los clientes en multijugador; no es algo que Quick Resume cause ni pueda corregir directamente.",
                "Esse é um problema ocasional no próprio teletransporte do PEAK Checkpoint Save, que afeta principalmente os clientes no multiplayer, não é algo que o Quick Resume causa ou pode corrigir diretamente.",
                "Это случайный сбой в собственной телепортации PEAK Checkpoint Save, в основном затрагивающий клиентов в многопользовательской игре, а не что-то, что вызывает или может напрямую исправить Quick Resume.",
                "Це випадковий збій у власній телепортації PEAK Checkpoint Save, який здебільшого зачіпає клієнтів у мультиплеєрі, а не те, що спричиняє або може напряму виправити Quick Resume.",
                "这是 PEAK Checkpoint Save 自身传送逻辑偶尔出现的小故障，主要影响多人游戏中的客户端，并非 Quick Resume 造成的问题，也不是它能直接修复的问题。",
                "",
                "これは PEAK Checkpoint Save 自体のテレポート処理でまれに起きる不具合で、主にマルチプレイのクライアント側で発生します。Quick Resume が原因ではなく、直接修正することもできません。",
                "이것은 PEAK Checkpoint Save 자체의 텔레포트 로직에서 가끔 발생하는 문제로, 주로 멀티플레이어의 클라이언트에게 영향을 미치며, Quick Resume이 유발하거나 직접 고칠 수 있는 문제가 아닙니다.",
                "To sporadyczny problem w samej teleportacji PEAK Checkpoint Save, dotykający głównie klientów w trybie wieloosobowym - to nie coś, co powoduje lub może bezpośrednio naprawić Quick Resume.",
                "Bu, PEAK Checkpoint Save'in kendi ışınlanma sisteminde ara sıra yaşanan, çoğunlukla çok oyunculu modda istemcileri etkileyen bir aksaklıktır; Quick Resume'un neden olduğu veya doğrudan düzeltebileceği bir şey değildir.",
            },
            [HelpText.OptimizedIntroFormat] = new[]
            {
                "In COOP, a plain load (native {0}, or {1}/Enter with no key held) already uses teleportJumpLogic {2} instead of your own base value (currently {3}) for exactly the reason above.",
                "En COOP, un chargement simple (natif {0}, ou {1}/Entrée sans touche maintenue) utilise déjà teleportJumpLogic {2} au lieu de votre propre valeur de base (actuellement {3}) pour exactement la raison ci-dessus.",
                "In COOP, un caricamento semplice (nativo {0}, oppure {1}/Invio senza tasti premuti) usa già teleportJumpLogic {2} invece del tuo valore base (attualmente {3}) proprio per il motivo sopra.",
                "In COOP verwendet ein normales Laden (nativ {0}, oder {1}/Enter ohne gehaltene Taste) bereits teleportJumpLogic {2} statt deines eigenen Basiswerts (derzeit {3}), genau aus dem oben genannten Grund.",
                "En COOP, una carga normal (nativa {0}, o {1}/Intro sin ninguna tecla pulsada) ya usa teleportJumpLogic {2} en lugar de tu propio valor base (actualmente {3}) exactamente por el motivo anterior.",
                "En COOP, una carga normal (nativa {0}, o {1}/Enter sin ninguna tecla presionada) ya usa teleportJumpLogic {2} en lugar de tu propio valor base (actualmente {3}) exactamente por el motivo anterior.",
                "No COOP, um carregamento simples (nativo {0}, ou {1}/Enter sem nenhuma tecla pressionada) já usa teleportJumpLogic {2} em vez do seu próprio valor base (atualmente {3}) exatamente pelo motivo acima.",
                "В КООПЕ обычная загрузка (родная {0}, или {1}/Enter без зажатой клавиши) уже использует teleportJumpLogic {2} вместо вашего собственного базового значения (сейчас {3}) именно по указанной выше причине.",
                "У КООПІ звичайне завантаження (рідне {0}, або {1}/Enter без затиснутої клавіші) вже використовує teleportJumpLogic {2} замість вашого власного базового значення (наразі {3}) саме з вищезазначеної причини.",
                "在合作模式下，普通加载（原生 {0}，或不按任何键的 {1}/回车）已经使用 teleportJumpLogic {2}，而不是你自己的基础值（当前为 {3}），正是出于上述原因。",
                "",
                "COOPでは、通常のロード（ネイティブの{0}、またはキーを押さない{1}/Enter）は、上記の理由からすでにteleportJumpLogic {2}を使用しており、あなた自身のベース値（現在{3}）は使われません。",
                "협동 모드에서는 일반 로드(네이티브 {0}, 또는 아무 키도 누르지 않은 {1}/Enter)가 위의 이유로 이미 당신의 기본값(현재 {3}) 대신 teleportJumpLogic {2}를 사용합니다.",
                "W trybie KOOP zwykłe wczytywanie (natywne {0}, lub {1}/Enter bez trzymanego klawisza) już używa teleportJumpLogic {2} zamiast twojej własnej wartości bazowej (obecnie {3}) właśnie z powyższego powodu.",
                "KOOP modunda, düz bir yükleme (yerel {0} veya hiçbir tuşa basılmadan {1}/Enter) tam da yukarıdaki nedenden dolayı zaten kendi temel değerin (şu anda {3}) yerine teleportJumpLogic {2} kullanır.",
            },
            [HelpText.OptimizedSoloNote] = new[]
            {
                "Solo is unaffected and always uses your own base value.",
                "Le mode solo n'est pas concerné et utilise toujours votre propre valeur de base.",
                "La modalità solo non è interessata e usa sempre il tuo valore base.",
                "Solo ist davon nicht betroffen und verwendet immer deinen eigenen Basiswert.",
                "El modo solo no se ve afectado y siempre usa tu propio valor base.",
                "El modo solo no se ve afectado y siempre usa tu propio valor base.",
                "O modo solo não é afetado e sempre usa seu próprio valor base.",
                "Одиночный режим не затрагивается и всегда использует ваше собственное базовое значение.",
                "Одиночний режим не зачіпається і завжди використовує ваше власне базове значення.",
                "单人模式不受影响，始终使用你自己的基础值。",
                "",
                "ソロプレイには影響せず、常にあなた自身のベース値が使用されます。",
                "솔로 플레이는 영향을 받지 않으며 항상 당신의 기본값을 사용합니다.",
                "Tryb solo nie jest tym objęty i zawsze używa twojej własnej wartości bazowej.",
                "Solo modu bundan etkilenmez ve her zaman kendi temel değerini kullanır.",
            },
            [HelpText.AskHostFormat] = new[]
            {
                "Ask your HOST to reload the SAME save from the save picker ({0}), while it's open, holding:",
                "Demandez à votre HÔTE de recharger la MÊME sauvegarde depuis le sélecteur de sauvegardes ({0}), pendant qu'il est ouvert, en maintenant :",
                "Chiedi al tuo HOST di ricaricare LO STESSO salvataggio dal selettore dei salvataggi ({0}), mentre è aperto, tenendo premuto:",
                "Bitte deinen HOST, denselben Speicherstand aus der Speicherstandauswahl ({0}) neu zu laden, während sie geöffnet ist, und dabei zu halten:",
                "Pide a tu HOST que recargue la MISMA partida desde el selector de partidas ({0}), mientras está abierto, manteniendo pulsado:",
                "Pide a tu HOST que recargue la MISMA partida desde el selector de partidas ({0}), mientras está abierto, manteniendo presionado:",
                "Peça ao seu HOST para recarregar o MESMO save pelo seletor de saves ({0}), enquanto ele está aberto, segurando:",
                "Попросите вашего ХОСТА перезагрузить ТО ЖЕ сохранение из выбора сохранений ({0}), пока оно открыто, удерживая:",
                "Попросіть вашого ХОСТА перезавантажити ТЕ САМЕ збереження з вибору збережень ({0}), поки воно відкрите, утримуючи:",
                "请让你的房主在存档选择器（{0}）打开的状态下，按住以下按键重新加载同一个存档：",
                "",
                "ホストに、セーブ選択画面（{0}）を開いた状態で以下のキーを押しながら同じセーブをリロードしてもらってください：",
                "호스트에게 저장 파일 선택 화면({0})이 열려 있는 동안 다음 키를 누른 채로 같은 저장 파일을 다시 불러오도록 요청하세요:",
                "Poproś swojego HOSTA, aby ponownie wczytał TEN SAM zapis z wyboru zapisów ({0}), gdy jest on otwarty, trzymając:",
                "SUNUCUNDAN, kayıt seçici ({0}) açıkken şunu basılı tutarak AYNI kaydı yeniden yüklemesini isteyin:",
            },
            [HelpText.ShiftLineFormat] = new[]
            {
                "{0} + {1}/Enter => your own base value ({2})",
                "{0} + {1}/Entrée => votre propre valeur de base ({2})",
                "{0} + {1}/Invio => il tuo valore base ({2})",
                "{0} + {1}/Enter => dein eigener Basiswert ({2})",
                "{0} + {1}/Intro => tu propio valor base ({2})",
                "{0} + {1}/Enter => tu propio valor base ({2})",
                "{0} + {1}/Enter => seu próprio valor base ({2})",
                "{0} + {1}/Enter => ваше собственное базовое значение ({2})",
                "{0} + {1}/Enter => ваше власне базове значення ({2})",
                "{0} + {1}/回车 => 你自己的基础值 ({2})",
                "",
                "{0} + {1}/Enter => あなた自身のベース値（{2}）",
                "{0} + {1}/Enter => 당신의 기본값 ({2})",
                "{0} + {1}/Enter => twoja własna wartość bazowa ({2})",
                "{0} + {1}/Enter => kendi temel değerin ({2})",
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
                "If it still happens, try Shift or Alt instead. This only affects the very next load and reverts back on its own after a bit.",
                "Si cela se produit encore, essayez plutôt Maj ou Alt. Cela n'affecte que le tout prochain chargement et revient automatiquement à la normale après un moment.",
                "Se succede ancora, prova invece Maiusc o Alt. Questo influisce solo sul prossimo caricamento e torna alla normalità da solo dopo un po'.",
                "Falls es weiterhin passiert, versuche stattdessen Umschalt oder Alt. Dies wirkt sich nur auf das allernächste Laden aus und setzt sich nach einer Weile von selbst zurück.",
                "Si sigue ocurriendo, prueba con Mayús o Alt en su lugar. Esto solo afecta a la próxima carga y vuelve a la normalidad por sí solo tras un momento.",
                "Si sigue ocurriendo, prueba con Shift o Alt en su lugar. Esto solo afecta a la próxima carga y vuelve a la normalidad por sí solo tras un momento.",
                "Se ainda acontecer, tente Shift ou Alt em vez disso. Isso afeta apenas o próximo carregamento e volta ao normal sozinho depois de um tempo.",
                "Если это всё ещё происходит, попробуйте вместо этого Shift или Alt. Это влияет только на самую следующую загрузку и само возвращается к норме через некоторое время.",
                "Якщо це все ще трапляється, спробуйте натомість Shift або Alt. Це впливає лише на найближче завантаження і саме повертається до норми за деякий час.",
                "如果问题仍然出现，可以改为尝试 Shift 或 Alt。这只影响下一次加载，之后会自动恢复。",
                "",
                "それでも起きる場合は、代わりにShiftまたはAltを試してください。これは次の1回のロードにのみ影響し、しばらくすると自動的に元に戻ります。",
                "그래도 계속 발생한다면 대신 Shift나 Alt를 시도해 보세요. 이는 바로 다음 로드에만 영향을 주며, 잠시 후 자동으로 원래대로 돌아갑니다.",
                "Jeśli to nadal się zdarza, spróbuj zamiast tego Shift lub Alt. Wpływa to tylko na najbliższe wczytanie i po chwili samo się cofa.",
                "Hâlâ oluyorsa, bunun yerine Shift veya Alt tuşunu deneyin. Bu yalnızca bir sonraki yüklemeyi etkiler ve bir süre sonra kendiliğinden eski haline döner.",
            },
            [HelpText.DisabledFooterNote] = new[]
            {
                "If one doesn't help, try the other. This only affects the very next load and reverts back on its own after a bit.",
                "Si l'un ne fonctionne pas, essayez l'autre. Cela n'affecte que le tout prochain chargement et revient automatiquement à la normale après un moment.",
                "Se uno non aiuta, prova l'altro. Questo influisce solo sul prossimo caricamento e torna alla normalità da solo dopo un po'.",
                "Wenn eines nicht hilft, versuche das andere. Dies wirkt sich nur auf das allernächste Laden aus und setzt sich nach einer Weile von selbst zurück.",
                "Si uno no funciona, prueba el otro. Esto solo afecta a la próxima carga y vuelve a la normalidad por sí solo tras un momento.",
                "Si uno no funciona, prueba el otro. Esto solo afecta a la próxima carga y vuelve a la normalidad por sí solo tras un momento.",
                "Se um não ajudar, tente o outro. Isso afeta apenas o próximo carregamento e volta ao normal sozinho depois de um tempo.",
                "Если один не помогает, попробуйте другой. Это влияет только на самую следующую загрузку и само возвращается к норме через некоторое время.",
                "Якщо один не допомагає, спробуйте інший. Це впливає лише на найближче завантаження і саме повертається до норми за деякий час.",
                "如果一个不管用，可以试试另一个。这只影响下一次加载，之后会自动恢复。",
                "",
                "片方で効果がなければ、もう片方を試してください。これは次の1回のロードにのみ影響し、しばらくすると自動的に元に戻ります。",
                "하나가 효과가 없다면 다른 것을 시도해 보세요. 이는 바로 다음 로드에만 영향을 주며, 잠시 후 자동으로 원래대로 돌아갑니다.",
                "Jeśli jedno nie pomoże, spróbuj drugiego. Wpływa to tylko na najbliższe wczytanie i po chwili samo się cofa.",
                "Biri işe yaramazsa diğerini deneyin. Bu yalnızca bir sonraki yüklemeyi etkiler ve bir süre sonra kendiliğinden eski haline döner.",
            },
            [HelpText.DisabledNoteFormat] = new[]
            {
                "COOP auto-optimization is currently OFF (enable-optimized-coop-loading in the config). Turning it on defaults a plain COOP load to teleportJumpLogic {0} instead, which extensive testing found avoids most of such issues listed above.",
                "L'auto-optimisation COOP est actuellement DÉSACTIVÉE (enable-optimized-coop-loading dans la config). L'activer fait qu'un chargement COOP simple utilise par défaut teleportJumpLogic {0}, ce qui, selon des tests approfondis, évite la plupart des problèmes listés ci-dessus.",
                "L'auto-ottimizzazione COOP è attualmente DISATTIVATA (enable-optimized-coop-loading nella config). Attivandola, un caricamento COOP semplice userà come predefinito teleportJumpLogic {0}, il che, secondo test approfonditi, evita la maggior parte dei problemi elencati sopra.",
                "Die COOP-Auto-Optimierung ist derzeit AUS (enable-optimized-coop-loading in der Konfiguration). Wird sie aktiviert, verwendet ein normales COOP-Laden standardmäßig teleportJumpLogic {0}, was laut ausführlichen Tests die meisten der oben genannten Probleme vermeidet.",
                "La auto-optimización de COOP está actualmente DESACTIVADA (enable-optimized-coop-loading en la configuración). Activarla hace que una carga COOP normal use por defecto teleportJumpLogic {0}, lo que, según pruebas exhaustivas, evita la mayoría de los problemas mencionados arriba.",
                "La auto-optimización de COOP está actualmente DESACTIVADA (enable-optimized-coop-loading en la configuración). Activarla hace que una carga COOP normal use por defecto teleportJumpLogic {0}, lo que, según pruebas exhaustivas, evita la mayoría de los problemas mencionados arriba.",
                "A auto-otimização do COOP está atualmente DESATIVADA (enable-optimized-coop-loading na configuração). Ativá-la faz com que um carregamento COOP simples use por padrão teleportJumpLogic {0}, o que, segundo testes extensivos, evita a maioria dos problemas listados acima.",
                "Автооптимизация КООПА сейчас ВЫКЛЮЧЕНА (enable-optimized-coop-loading в конфиге). Включив её, обычная загрузка в КООПЕ будет по умолчанию использовать teleportJumpLogic {0}, что, по результатам обширного тестирования, избегает большинства проблем, перечисленных выше.",
                "Автооптимізація КООПУ зараз ВИМКНЕНА (enable-optimized-coop-loading у конфізі). Увімкнувши її, звичайне завантаження в КООПІ використовуватиме за замовчуванням teleportJumpLogic {0}, що, за результатами масштабного тестування, дає змогу уникнути більшості проблем, перелічених вище.",
                "合作模式自动优化目前处于关闭状态（配置中的 enable-optimized-coop-loading）。开启后，普通的合作模式加载将默认使用 teleportJumpLogic {0}，大量测试表明这能避免上述大多数问题。",
                "",
                "COOP自動最適化は現在OFFです（設定のenable-optimized-coop-loading）。有効にすると、通常のCOOPロードはデフォルトでteleportJumpLogic {0}を使用するようになり、これは大規模なテストの結果、上記の問題のほとんどを回避できることが分かっています。",
                "협동 자동 최적화가 현재 꺼져 있습니다 (설정의 enable-optimized-coop-loading). 이를 켜면 일반 협동 로드가 기본적으로 teleportJumpLogic {0}을(를) 사용하게 되며, 광범위한 테스트 결과 위에 나열된 대부분의 문제를 피할 수 있는 것으로 확인되었습니다.",
                "Automatyczna optymalizacja KOOP jest obecnie WYŁĄCZONA (enable-optimized-coop-loading w konfiguracji). Włączenie jej sprawia, że zwykłe wczytywanie KOOP domyślnie używa teleportJumpLogic {0}, co - jak wykazały obszerne testy - pozwala uniknąć większości problemów wymienionych powyżej.",
                "KOOP otomatik optimizasyonu şu anda KAPALI (yapılandırmada enable-optimized-coop-loading). Açmak, düz bir KOOP yüklemesinin varsayılan olarak teleportJumpLogic {0} kullanmasını sağlar; kapsamlı testler bunun yukarıda listelenen sorunların çoğunu önlediğini gösterdi.",
            },
            [HelpText.AchievementsNote] = new[]
            {
                "Loading a save may grant Steam achievements, skip it if you want to earn everything unassisted.",
                "Charger une sauvegarde peut débloquer des succès Steam, évitez-le si vous voulez tout obtenir sans assistance.",
                "Caricare un salvataggio potrebbe sbloccare obiettivi Steam, evitalo se vuoi ottenere tutto senza assistenza.",
                "Das Laden eines Speicherstands kann Steam-Erfolge freischalten - überspringe es, wenn du alles ohne Hilfe erspielen möchtest.",
                "Cargar una partida puede otorgar logros de Steam; evítalo si quieres conseguirlo todo sin ayuda.",
                "Cargar una partida puede otorgar logros de Steam; evítalo si quieres conseguirlo todo sin ayuda.",
                "Carregar um save pode conceder conquistas da Steam; evite isso se quiser conquistar tudo sem ajuda.",
                "Загрузка сохранения может дать достижения Steam, пропустите это, если хотите получить всё без посторонней помощи.",
                "Завантаження збереження може дати досягнення Steam, пропустіть це, якщо хочете отримати все без сторонньої допомоги.",
                "加载存档可能会解锁 Steam 成就，如果你想在不借助辅助的情况下获得所有成就，请跳过此功能。",
                "",
                "セーブをロードするとSteam実績が解除される場合があります。何も使わずに実績をすべて獲得したい場合は使用しないでください。",
                "저장 파일을 불러오면 Steam 업적이 부여될 수 있습니다. 도움 없이 모든 업적을 획득하고 싶다면 사용하지 마세요.",
                "Wczytanie zapisu może przyznać osiągnięcia Steam - pomiń to, jeśli chcesz zdobyć wszystko bez wspomagania.",
                "Bir kaydı yüklemek Steam başarımları verebilir; her şeyi yardımsız kazanmak istiyorsan bunu atla.",
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(HelpText key) => LocalizationHelper.Resolve(_table[key]);

        /// <summary>Same as <see cref="Get(HelpText)"/>, then <see cref="string.Format(string, object[])"/>'d against <paramref name="args"/></summary>
        public static string Get(HelpText key, params object[] args) => string.Format(Get(key), args);
    }
}
