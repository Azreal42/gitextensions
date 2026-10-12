# 0025 — Présentation du diff et patch canonique

Décision : conserver les palettes, la reconnaissance des lignes déplacées et l’atténuation des fragments inchangés du viewer original. Les couleurs Git configurées par le développeur restent prioritaires ; les valeurs manquantes sont fournies au processus, sans écrire sa configuration.

Le patch destiné à l’index reste indépendant du texte affiché. Une sélection partielle n’est applicable que si les offsets du patch et de la présentation coïncident exactement. Word Diff, Difftastic, combined et range-diff restent en lecture seule ; une modification du fichier entre les deux lectures Git interdit aussi cette sélection.

Les réglages de couleur sont partagés entre viewers. L’apparence est temporaire, sauf choix explicite de la mémoriser ; ouvrir un nouveau viewer recharge alors l’apparence enregistrée, comme dans l’original. Difftastic utilise la commande `difftool.difftastic.cmd` existante et ses numéros rejoignent les colonnes du viewer.

Les raccourcis du viewer focalisé ont priorité sur ceux de Browse quand leur action est disponible ; hors du viewer, les commandes historiques du navigateur restent accessibles.

Repères : `GitRepository.DiffDocuments`, `GitAnsiColorParser`, `GitDiffInlineHighlighter`, `GitDifftasticFormatter`, `DiffView`, `DiffViewerColors`, `DiffViewerPreferencesStore`, `DiffViewerAppearanceMenu`.
