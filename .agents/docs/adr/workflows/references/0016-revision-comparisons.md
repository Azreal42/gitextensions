# ADR 0016 · Comparer les révisions

Statut : accepté sur le périmètre décrit ; FormDiff reste N2.

Besoin : comparer BASE/A/B sans remplacer l’aperçu de Browse, avec les choix et gestes de l’original.

Décision : dialogue dédié et endpoints commit/index/worktree typés ; BASE initiale et HEAD de topologie figés à l’ouverture, index/worktree restant dynamiques. Changer ou échanger les sélecteurs ne recalcule pas la BASE initiale de la case. Chemins renommés et non suivis littéraux ; relire après un outil externe, même en échec/annulation. Démarrer les lectures après établissement du propriétaire.

Le raccord Browse des sélections réelles et artificielles communes, de leur ordre WinForms et de leurs parents typés est décrit dans [ADR0155](../history/0155-browse-shared-snapshot.md), qualification H35 par suites complètes et paquet isolé ; parité complète ouverte.

A→B reste principal. Si une BASE commune diffère des deux extrémités et que ShowAll est activé, présenter A→B, Range (si Git le permet), BASE→B, BASE→A dans cet ordre. La préférence globale, activée par défaut, est commune à Browse/Compare et relue au calcul affiché.

Chaque groupe conserve sa paire typée : lecture, blobs, enregistrement et outil fichier suivent le groupe choisi ; l’outil de dossier suit la paire effective du formulaire. Range capture ses vrais commits et garde un lecteur dédié, sans faux fichier, vue blob ou action fichier ; le filtre de liste ne devient pas filtre de contenu Range.

Browse, Commit et Compare partagent FileStatusList : six modes, regex/MRU, renommages, densité 18 px et préférences. Dans Compare, une feuille fichier unique alimente les vues fichier et Range son lecteur dédié ; parents/multisélections les vident. La racine unique sans enfant est automatiquement sélectionnée comme sous Windows, sans descendre dans un parent. Dans Browse, le groupe ordinaire filtré vide efface contenu/identité ; résumés de dossiers, groupes non vides et RangeDiff gardent leur comportement propre.

Les lectures ciblées laissent filtre/liste utilisables : réutiliser la même sélection, annuler la précédente et refuser les publications/erreurs périmées. Une opération globale ou la fermeture annule et draine ces lectures. Aucun retour du focus lors d’une reconstruction. Après une action de mode, reprendre seulement son origine encore valide ; abandonner l’ancienne origine pointeur quand le clavier prend la suite. Le décor de ligne ne prend pas le focus, et les palettes restent locales au contrôle.

Ce menu Settings reprend son origine filtre/liste après action ou fermeture, y compris Escape, seulement si comparaison, filtre et destination de focus restent valides. Conserver caret/sélection de l’éditeur ; ne pas étendre ce retour aux autres popups ou aux rebuilds.

Create/Checkout/Rename partagent normalisation, gardes et préférences ; Rename conserve les filtres de navigation.

Les menus métier et leurs métadonnées sont communs à Browse/Compare ; chaque feuille garde sa paire et les mutations restent ciblées ([ADR 0080](../../ui/0080-shared-file-menus.md)). Restent scripts/Visual Studio, découverte/réglages complets des outils, variantes de rendu, gestes de plage clavier sans oracle Windows validé et plateformes étendues. Pas de parité globale certifiée ; FormDiff reste N2.

Repères : `CompareRevisionsDialog/Selection/Groups`, `ComparisonRevisionSelector`, `CompareBranchPicker`, `GitRepository.Comparisons/ComparisonGroups`, `GitComparisonGroup`, `RevisionDiffPreferencesStore`, `GitBranchNaming`, `RenameBranchDialog`, `MainWindow.Comparisons/Diffs`, `FileStatusListView/Builder`, `FileStatusListPreferencesStore`. Source, tests et preuves : [TODO](../../../todo.md).
