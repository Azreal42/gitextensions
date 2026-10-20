# ADR 0141 · Retrouver la géométrie du journal

FormGitCommandLog Windows active enablePositionRestore. Le journal portable doit retrouver sa taille et sa position normales lors d’une nouvelle ouverture, ainsi que son état maximisé. Minimisé n’est pas un état à rejouer : la réouverture revient à Normal. F12 sur une instance vivante garde son activation existante et ne recharge pas la géométrie sauvegardée.

L’état réside dans un fichier XDG dédié, séparé des Settings, du cache et de Capture call stacks. Seule la géométrie du journal est éditée ; autres fenêtres et champs inconnus sont conservés. Onglet, wrap, Topmost et splitters ne deviennent pas des préférences persistantes. Un état illisible ou invalide doit rester intact ; un profil non vérifié n’autorise pas une sauvegarde dans le dépôt.

Le placement doit rester accessible après changement d’écran/DPI. Dimensions client DIP et ajustement de visibilité sont une adaptation Linux ; multi-écrans, DPI et placement natif Wayland restent à qualifier.

Source `7097618c1` : compilation/ciblés/HOME-XDG, suites complètes, natif source borné et paquet isolé vérifiés séparément ; [preuves et limites](../../../todo.md#g%C3%A9om%C3%A9trie-du-journal). [Revue native](../../../../../artifacts/linux/command-log-geometry-20261020/review/root-native-review.json) : persistance, pas premier mapping/hot-DPI/Wayland. L22 reste lié à C2 ; N2/aspect inchangés, treize familles ouvertes, aucun N4.
