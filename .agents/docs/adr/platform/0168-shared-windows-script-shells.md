# ADR 0168 · Shell Windows commun aux outils et scripts

Statut : source compilé et contrats ciblés exécutés sur Linux ; exécution Windows requise.

WindowsScriptShellResolver partage la recherche entre Tools et DesktopScriptExecutor. Pour les seuls alias nus bash/bash.exe/sh/sh.exe, les outils explicites puis l'installation du Git capturé fournissent le shell demandé. Un bash absent ne devient pas sh ou le bash WSL ambiant. Les chemins explicites et autres programmes restent littéraux. La recherche reconnaît cmd, bin, usr/bin et mingw32/64/bin ; le repli du lanceur interactif utilise seulement le PATH capturé, sans adopter un git-bash.exe du dépôt courant.

Desktop résout le programme avant ses chemins foreground/background/NavigateTo ; un NavigateTo vide et les cibles PowerShell/OpenUrl restent inchangés. Le runner générique ne réinterprète pas les commandes utilisateur. GitConfiguredExecutable conserve son comportement normal ; son overload explicite sert à partager et tester les règles Windows.

Douze contrats portables couvrent les alias, outils explicites, chemins littéraux, PATH cité et dépôt courant. Deux parcours natifs Windows exécutent le vrai Bash du Git sélectionné avec un PATH sans usr/bin : arguments UTF-8/NUL séparés, environnement, répertoire et sorties0/73. La CI Windows sélectionne ces deux cas ; ils sont compilés hors Windows avec CompileWindowsConsoleTests, sans prétendre les y exécuter.

Le lot combiné avec ADR0169 compile sans avertissement/erreur : 14 POSIX, 236 Desktop ciblés, deux régressions de statut, 1 210 Core, 42 Plugins et cinq contrôles Debian passent sur Linux, aucun ignoré. Les 213 anciennes identités du ciblé restent présentes. Preuves : artifacts/cross-platform/20261104/shell-discovery/validation-review.json et frozen-source-hashes.json. La suite Desktop complète et le paquet exact de ce nouveau lot restent à vérifier séparément. Aucun résultat Windows/Mac récent : GitHub refuse les nouveaux runs HTTP422 malgré enabled=true.
