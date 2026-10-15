# ADR 0060 · Menus classiques partagés

## Besoin

Rapprocher Tools et Help des captures Windows et réutiliser les mêmes éléments dans les menus de l’application. Prendre les définitions ToolsToolStripMenuItem/HelpToolStripMenuItem du code original comme référence ; leurs traductions XLF se trouvent dans FormBrowse.

## Décision

Charger ClassicMenuAppearance une fois depuis App après Fluent et ClassicStyles. Il remplace RevisionContextMenuAppearance et DiffViewerPopupLayout : palette originale claire/sombre, police 12, lignes 22 px, séparateurs 6 px avec filet décalé, bordure carrée de 1 px, icônes 16 px et colonnes natives de raccourcis/chevrons. Les boutons horizontaux du menu gardent leur disposition native ; les descendants de MenuBase reçoivent le style de popup, y compris les éléments ajoutés après ouverture.

Retirer les dimensions et palettes locales des menus Appearance, Comparison, Rendering et Reading du diff. Conserver les templates, interactions clavier, cases/radios et automation peers Avalonia. Une case sans icône partage sa gouttière ; quand une entrée porte aussi une icône originale, la colonne native de coche reste distincte afin de ne pas remplacer le PNG. Les tests de pixels des glyphes et coches restent actifs. OriginalMenuItems regroupe icônes, access keys XLF et raccourcis ; Translate réutilise la correction monochrome existante en sombre.

Tools reprend Git bash/Ctrl+G, Git GUI, GitK, Git command log/F12 et Settings/Ctrl+virgule, avec les séparateurs historiques. PuTTY et son séparateur restent masqués hors Windows, comme le code original. Les outils utilisent un ProcessStartInfo avec répertoire du dépôt et arguments littéraux ; GUI est refusé en bare. Les erreurs de lancement sont visibles. Les paramètres Avalonia sauvegardent les préférences existantes (thème, limite d’historique, normalisation), en conservant les autres champs et sans mutation après Cancel. Un bouton conserve l’accès à la configuration Git existante.

Help reprend les huit entrées de la capture. Manuel, traduction et don pointent aux ressources historiques ; le signalement et les versions pointent au fork Linux. Changelog lit ChangeLog.md original embarqué, About montre la vraie version d’assembly. Le signalement copie seulement version/OS/runtime/architecture. Check for updates ouvre les releases du fork : cela n’implémente pas de mise à jour automatique. L’entrée télémétrie ouvre les paramètres, comme l’original, et reste décochée ; aucun collecteur ni consentement fictif n’est ajouté.

Le journal Git est non modal et réutilisé par F12. GitRunner enregistre dans GitCommandLog au plus 500 fins de commandes : verbe reconnu, répertoire, heure, durée, exit code ou annulation. Aucun argument, environnement, stdin ou contenu de sortie n’est retenu ou écrit sur disque. Clear efface seulement ce journal ; fermer sa fenêtre arrête son timer.

## Limites

Les paramètres Windows complets, l’updater automatique, le service de télémétrie, les raccourcis configurables et la parité fonctionnelle de toutes les autres entrées historiques restent ouverts. Les rendus headless et les smoke tests du paquet ne clôturent pas l’audit natif étendu. Les 13 familles de migration restent ouvertes ; preuves et état du paquet dans le [TODO](../../todo.md).
