# ADR 0023 · Lire les merges et comparer les séries

Statut : accepté.

Besoin : retrouver les groupes de comparaison de l’original, pour inspecter une résolution de merge et vérifier ce qui change après une réécriture de série.

Décision : garder l’arbre à gauche et le lecteur à droite, les icônes et palettes historiques. Un merge montre chaque parent puis le diff combiné s’il existe. Les colonnes distinguent les parents et le résultat. Les options tous parents et omission des hunks non intéressants gardent leurs défauts historiques et sont enregistrées dans XDG.

Pour les séries, conserver le primaire et l’ordre de sélection, même si le graphe les ordonne autrement. Trois sélections peuvent imposer une BASE antérieure ; quatre conservent deux bornes distinctes. Corriger la perte de ces bornes dans l’original. F5 conserve l’ordre et le groupe actif. Le contexte commence à trois lignes ; zéro donne le résumé de série. Alt+↑/↓ navigue entre les commits du range-diff.

Le range-diff et le combined sont des lectures ; ne pas les indexer comme patches ordinaires. Les aperçus de fichiers restent du texte, même lorsqu’ils contiennent une syntaxe de patch. Chemins littéraux et octets de contenu ne changent pas.

Repères : `MainWindow.Diffs`, `MainWindow.RangeDiff`, `DiffView`, `GitRepository.MergeDiffs`, `GitRepository.RangeDiffs`. Classification A/B des fichiers, ANSI configurable et différences intra-ligne restent ouverts.
