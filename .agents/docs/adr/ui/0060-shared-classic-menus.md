# ADR 0060 · Menus classiques partagés

## Besoin

Rapprocher Tools et Help des captures Windows et réutiliser les mêmes éléments dans les menus de l’application. Prendre les définitions ToolsToolStripMenuItem/HelpToolStripMenuItem du code original comme référence ; leurs traductions XLF se trouvent dans FormBrowse.

## Décision

Charger ClassicMenuAppearance une fois depuis App après Fluent et ClassicStyles. Il remplace RevisionContextMenuAppearance et DiffViewerPopupLayout : palette originale claire/sombre, police 12, lignes 22 px, séparateurs 6 px avec filet décalé, bordure carrée de 1 px, icônes 16 px et colonnes natives de raccourcis/chevrons. Les boutons horizontaux du menu gardent leur disposition native ; les descendants de MenuBase reçoivent le style de popup, y compris les éléments ajoutés après ouverture.

Retirer les dimensions et palettes locales des menus Appearance, Comparison, Rendering et Reading du diff. Conserver les templates, interactions clavier, cases/radios et automation peers Avalonia. Une case sans icône partage sa gouttière ; quand une entrée porte aussi une icône originale, la colonne native de coche reste distincte afin de ne pas remplacer le PNG. Les tests de pixels des glyphes et coches restent actifs. OriginalMenuItems regroupe icônes, access keys XLF et raccourcis ; Translate réutilise la correction achromatique existante en sombre, y compris RGB 1 et alpha partiel.

Tools reprend Git bash/Ctrl+G, Git GUI, GitK, Git command log/F12 et Settings/Ctrl+virgule, avec les séparateurs historiques. PuTTY et son séparateur restent masqués hors Windows, comme le code original. Les outils utilisent un ProcessStartInfo avec répertoire du dépôt et arguments littéraux ; GUI est refusé en bare. Les erreurs de lancement sont visibles. Les paramètres Avalonia sauvegardent les préférences existantes (thème, limite d’historique, normalisation), en conservant les autres champs et sans mutation après Cancel. Un bouton conserve l’accès à la configuration Git existante.

Help reprend les huit entrées de la capture. Manuel, traduction et don pointent aux ressources historiques ; le signalement et les versions pointent au fork Linux. Changelog lit ChangeLog.md original embarqué, About montre la vraie version d’assembly. Le signalement copie seulement version/OS/runtime/architecture. Check for updates ouvre les releases du fork : cela n’implémente pas de mise à jour automatique. L’entrée télémétrie ouvre les paramètres, comme l’original, et reste décochée ; aucun collecteur ni consentement fictif n’est ajouté.

Le journal Git est non modal, global et réutilisé par F12. GitRunner publie dans GitCommandLog au plus 500 commandes, du démarrage au PID confirmé puis à la fin : verbe reconnu, répertoire, heure, durée, état running, exit code ou annulation. Des identités stables et snapshots immuables conservent l’ordre de lancement ; Changed remplace le timer et la fenêtre retire son abonnement à la fermeture. Clear efface seulement ce journal et empêche toute résurrection tardive, sans arrêter Git. Aucun argument, environnement, stdin, contenu de sortie ou message d’exception n’est retenu ; seul un export explicite des métadonnées sûres écrit un fichier. Le cycle, les interactions et les limites du périmètre sont définis par [ADR0138](dialogs/0138-command-journal-lifecycle.md), avec preuves et portée dans le TODO.

## Limites

Les paramètres Windows complets, l’updater automatique, le service de télémétrie, les raccourcis configurables et la parité fonctionnelle de toutes les autres entrées historiques restent ouverts. Les rendus headless et les smoke tests du paquet ne clôturent pas l’audit natif étendu. Les 13 familles de migration restent ouvertes ; preuves et état du paquet dans le [TODO](../../todo.md).
