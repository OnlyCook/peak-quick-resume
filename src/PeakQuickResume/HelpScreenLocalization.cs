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
        QuickResumeFormat,     // {0} = resume key badge (used twice in the sentence)
        RestartTip,
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
            [HelpText.QuickResumeFormat] = new[]
            {
                "Press {0} ANYWHERE to open the save picker, arrow keys to choose, {0}/Enter to load (press it twice for your latest).",
                "Appuyez sur {0} N'IMPORTE OÙ pour ouvrir le sélecteur de sauvegardes, les flèches pour choisir, {0}/Entrée pour charger (appuyez-y deux fois pour la plus récente).",
                "Premi {0} OVUNQUE per aprire il selettore dei salvataggi, le frecce per scegliere, {0}/Invio per caricare (premilo due volte per il più recente).",
                "Drücke {0} ÜBERALL, um die Speicherstandauswahl zu öffnen, Pfeiltasten zum Auswählen, {0}/Enter zum Laden (zweimal drücken für den neuesten).",
                "Pulsa {0} EN CUALQUIER LUGAR para abrir el selector de partidas, flechas para elegir, {0}/Intro para cargar (púlsalo dos veces para la más reciente).",
                "Presiona {0} EN CUALQUIER LUGAR para abrir el selector de partidas, flechas para elegir, {0}/Enter para cargar (presiónalo dos veces para la más reciente).",
                "Pressione {0} EM QUALQUER LUGAR para abrir o seletor de saves, setas para escolher, {0}/Enter para carregar (pressione duas vezes para o mais recente).",
                "Нажмите {0} В ЛЮБОМ МЕСТЕ, чтобы открыть выбор сохранений, стрелки для выбора, {0}/Enter для загрузки (нажмите дважды для последнего).",
                "Натисніть {0} БУДЬ-ДЕ, щоб відкрити вибір збережень, стрілки для вибору, {0}/Enter для завантаження (натисніть двічі для останнього).",
                "在任何地方按下 {0} 打开存档选择器，方向键选择，{0}/回车加载（连按两次加载最新存档）。",
                "",
                "どこでも {0} を押すとセーブ選択画面が開きます。矢印キーで選択し、{0}/Enterでロード（2回押すと最新のセーブをロード）。",
                "어디서든 {0}을(를) 눌러 저장 파일 선택 화면을 열고, 방향키로 선택한 뒤 {0}/Enter로 불러오세요 (두 번 누르면 최신 저장 파일을 불러옵니다).",
                "Naciśnij {0} GDZIEKOLWIEK, aby otworzyć wybór zapisów, strzałki do wyboru, {0}/Enter do wczytania (naciśnij dwukrotnie, aby wczytać najnowszy).",
                "Kayıt seçiciyi açmak için HER YERDE {0} tuşuna basın, seçim için ok tuşları, yüklemek için {0}/Enter (en sonuncusu için iki kez basın).",
            },
            [HelpText.RestartTip] = new[]
            {
                "Did something go wrong after loading? (blank map, bouncing up and down, falling through the floor, or you never actually reached your campfire)\nHave everyone quit and rejoin (or fully restart) the game, then load the same save again. This alone fixes most issues.",
                "Un problème est survenu après le chargement ? (carte vide, rebonds de haut en bas, chute à travers le sol, ou vous n'avez en fait jamais atteint votre feu de camp)\nQue tout le monde quitte et revienne (ou redémarre complètement) le jeu, puis rechargez la même sauvegarde. Cela suffit à résoudre la plupart des problèmes.",
                "Qualcosa è andato storto dopo il caricamento? (mappa vuota, rimbalzi su e giù, caduta attraverso il pavimento, oppure non hai mai effettivamente raggiunto il tuo falò)\nFai in modo che tutti escano e rientrino (o riavviino completamente) il gioco, poi ricarica lo stesso salvataggio. Questo da solo risolve la maggior parte dei problemi.",
                "Ist nach dem Laden etwas schiefgelaufen? (leere Karte, Auf-und-ab-Springen, Durchfallen durch den Boden, oder du hast dein Lagerfeuer eigentlich nie erreicht)\nLasst alle das Spiel verlassen und wieder beitreten (oder komplett neu starten) und ladet dann denselben Speicherstand erneut. Das allein behebt die meisten Probleme.",
                "¿Algo salió mal después de cargar? (mapa vacío, rebotando arriba y abajo, cayendo a través del suelo, o en realidad nunca llegaste a tu hoguera)\nQue todos salgan y vuelvan a entrar (o reinicien completamente) el juego, y luego recarguen la misma partida. Esto por sí solo soluciona la mayoría de los problemas.",
                "¿Algo salió mal después de cargar? (mapa vacío, rebotando arriba y abajo, cayendo a través del suelo, o en realidad nunca llegaste a tu fogata)\nQue todos salgan y vuelvan a entrar (o reinicien completamente) el juego, y luego recarguen la misma partida. Esto por sí solo soluciona la mayoría de los problemas.",
                "Algo deu errado depois de carregar? (mapa vazio, quicando para cima e para baixo, caindo através do chão, ou você nunca chegou realmente à sua fogueira)\nFaça todos saírem e entrarem novamente (ou reiniciarem completamente) o jogo, depois recarreguem o mesmo save. Isso sozinho resolve a maioria dos problemas.",
                "Что-то пошло не так после загрузки? (пустая карта, подпрыгивание вверх-вниз, падение сквозь пол, или вы так и не добрались до костра)\nПусть все выйдут и снова зайдут в игру (или полностью перезапустят её), а затем загрузите то же сохранение. Одно это решает большинство проблем.",
                "Щось пішло не так після завантаження? (порожня карта, підстрибування вгору-вниз, падіння крізь підлогу, або ви так і не дісталися до вогнища)\nНехай усі вийдуть і знову зайдуть у гру (або повністю перезапустять її), а потім завантажте те саме збереження. Одне це вирішує більшість проблем.",
                "加载后出问题了吗？（地图空白、上下弹跳、穿过地板掉落，或者其实根本没有到达篝火旁）\n让所有人退出并重新加入游戏（或彻底重启游戏），然后再加载同一个存档。仅此一步就能解决大多数问题。",
                "",
                "ロード後に問題が起きましたか？（マップが空、上下に跳ね続ける、床をすり抜けて落下する、または実際には焚き火にたどり着いていない）\n全員が一度ゲームを退出して再参加する（またはゲームを完全に再起動する）、その後同じセーブをロードし直してください。これだけでほとんどの問題が解決します。",
                "불러온 뒤 문제가 발생했나요? (빈 맵, 위아래로 튕김, 바닥을 뚫고 떨어짐, 또는 실제로 모닥불에 도착하지 않음)\n모두가 게임을 나갔다가 다시 참가하거나(또는 게임을 완전히 재시작) 한 뒤, 같은 저장 파일을 다시 불러오세요. 이것만으로 대부분의 문제가 해결됩니다.",
                "Czy po wczytaniu coś poszło nie tak? (pusta mapa, odbijanie się góra-dół, przepadanie przez podłogę, albo w ogóle nie dotarłeś do ogniska)\nNiech wszyscy opuszczą grę i dołączą ponownie (albo całkowicie ją zrestartują), a następnie wczytajcie ten sam zapis. Samo to rozwiązuje większość problemów.",
                "Yükledikten sonra bir şeyler ters mi gitti? (boş harita, yukarı aşağı zıplama, zeminin içinden düşme, veya aslında hiç kamp ateşine ulaşmama)\nHerkesin oyundan çıkıp yeniden katılmasını (veya oyunu tamamen yeniden başlatmasını) sağlayın, ardından aynı kaydı tekrar yükleyin. Bu tek başına çoğu sorunu çözer.",
            },
            [HelpText.AchievementsNote] = new[]
            {
                "Achievement progress is now saved and restored correctly when you load a checkpoint - but only for players who have PEAK Quick Resume installed themselves.",
                "La progression des succès est désormais sauvegardée et restaurée correctement lorsque vous chargez un point de contrôle - mais seulement pour les joueurs ayant eux-mêmes PEAK Quick Resume installé.",
                "I progressi degli obiettivi vengono ora salvati e ripristinati correttamente quando carichi un checkpoint - ma solo per i giocatori che hanno PEAK Quick Resume installato personalmente.",
                "Der Erfolgsfortschritt wird jetzt beim Laden eines Checkpoints korrekt gespeichert und wiederhergestellt - aber nur für Spieler, die PEAK Quick Resume selbst installiert haben.",
                "El progreso de los logros ahora se guarda y se restaura correctamente al cargar un punto de control - pero solo para los jugadores que tengan PEAK Quick Resume instalado ellos mismos.",
                "El progreso de los logros ahora se guarda y se restaura correctamente al cargar un punto de control - pero solo para los jugadores que tengan PEAK Quick Resume instalado ellos mismos.",
                "O progresso das conquistas agora é salvo e restaurado corretamente ao carregar um checkpoint - mas apenas para jogadores que tiverem o PEAK Quick Resume instalado.",
                "Прогресс достижений теперь корректно сохраняется и восстанавливается при загрузке чекпоинта - но только для игроков, у которых сам установлен PEAK Quick Resume.",
                "Прогрес досягнень тепер коректно зберігається та відновлюється під час завантаження чекпоінта - але лише для гравців, які самі встановили PEAK Quick Resume.",
                "现在加载存档点时，成就进度会被正确保存和恢复——但仅限自己安装了 PEAK Quick Resume 的玩家。",
                "",
                "チェックポイントをロードすると、実績の進行状況が正しく保存・復元されるようになりました。ただし、これはPEAK Quick Resumeを自分自身でインストールしているプレイヤーにのみ適用されます。",
                "이제 체크포인트를 불러올 때 업적 진행 상황이 올바르게 저장 및 복원됩니다 - 단, 본인이 직접 PEAK Quick Resume를 설치한 플레이어에게만 해당됩니다.",
                "Postęp osiągnięć jest teraz poprawnie zapisywany i przywracany podczas wczytywania punktu kontrolnego - ale tylko dla graczy, którzy sami mają zainstalowany PEAK Quick Resume.",
                "Bir kontrol noktası yüklediğinizde başarım ilerlemesi artık doğru şekilde kaydediliyor ve geri yükleniyor - ancak bu yalnızca PEAK Quick Resume'u kendisi yüklemiş oyuncular için geçerlidir.",
            },
        };

        /// <summary>Text for the current value of <see cref="LocalizedText.CURRENT_LANGUAGE"/></summary>
        public static string Get(HelpText key) => LocalizationHelper.Resolve(_table[key]);

        /// <summary>Same as <see cref="Get(HelpText)"/>, then <see cref="string.Format(string, object[])"/>'d against <paramref name="args"/></summary>
        public static string Get(HelpText key, params object[] args) => string.Format(Get(key), args);
    }
}
