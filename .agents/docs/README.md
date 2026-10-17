# Repères

But : proposer les workflows Git Extensions sur un bureau Linux, installables en `.deb`, en gardant la disposition, les palettes, les icônes et les gestes de l’application originale. La migration reste ouverte tant que les workflows existants ne sont pas couverts.

- [C4](c4.md) : périmètre et points d'entrée.
- [Décisions](adr/) : classées par sujet/sous-sujet.
- [TODO](todo.md) : périmètre restant et état de livraison.
- [Inventaire Windows → Linux](windows-linux-window-inventory.md) : chaque fenêtre/page, équivalent réel, niveau N0–N4, écarts et preuves ; CSV/JSON et contrôle reproductible ; dialogues de l’installateur et travaux non validés séparés.
- [Failures](failure.md) : causes et pièges à ne pas reproduire.
- [Menus de fichiers et récupération Skip/Assume](../../artifacts/linux/snapshots/a0ba1c140/proofs/source-validation.json) et [paquet isolé](../../artifacts/linux/snapshots/a0ba1c140/proofs/package-runtime-verification.json) : Desktop complet et références Core/Plugins distinctes ; périmètre natif dans le TODO. FormCommit/Browse/FormDiff restent N2, aucun N4 ; installation hôte distincte.
- [Tri et priorités](adr/preferences/sorting/0082-native-reference-sorting.md) : cinq réglages Windows et GitDefault sans flag ; page Sorting N2, validations et limites dans le TODO.
- [Informations de commit partagées](adr/ui/history/0083-shared-commit-information.md) : détails, notes et références contenant dans Browse, historique fichier et Blame ; [suites complètes](../../artifacts/linux/commit-information-20261017/full-02/parent-verification.json) et [paquet exact](../../artifacts/linux/snapshots/b356d5f67-package03/proofs/log-finalized-parent-verification-05/package-parent-verification.json) vérifiés. [Natif Browse anglais/clair/X11](../../artifacts/linux/snapshots/b356d5f67/proofs/native-commit-information-parent-review.json) ciblé ; autres hôtes/variantes et accessibilité ouverts, niveaux inchangés.
- [Sockets Unix et TMPDIR long](adr/platform/git/0084-bounded-unix-sockets.md) : lancement Git et console interactive avec répertoire privé borné en UTF-8 ; suites complètes et paquet exact vérifiés, ouverture X11 et processus réels sous TMPDIR longs ; niveaux inchangés.

- [Historique fichier dans Browse](adr/workflows/files/0085-browse-file-history.md) : processus indépendant par défaut, présélection sans borne, noms suivis depuis HEAD et vrais endpoints ; Blame inline avec publication atomique. [Full02 vérifié](../../artifacts/linux/file-history-browse-20261017/full-02/parent-verification.json), [paquet exact vérifié isolément](../../artifacts/linux/snapshots/1e6cc163a/proofs/package-parent-verification.json), [revue native parent](../../artifacts/linux/snapshots/1e6cc163a/proofs/native-file-history-parent-review.json) ; niveaux inchangés.

- [Réglages partagés d’historique fichier](adr/workflows/files/0086-shared-file-history-settings.md) : quatre préférences et F5 des paires typées ; Git/headless et paquet vérifiés, audit GUI natif ouvert.

Le code, les tests et les scripts sont la source de vérité. Ces notes gardent l'intention et les chemins utiles ; les logs, captures, checksums et preuves historiques restent dans `artifacts/linux/`, avec leur source d'origine.

- [Recherche incrémentale de grille](adr/ui/history/0087-revision-quick-search.md) : contrat Windows, messages complets et destinations typées ; validation et limites dans le TODO.

- [Navigation parent/enfant](adr/ui/history/0088-parent-child-navigation.md) : parcours réversible Windows, voisins du graphe affiché et focus fichier ; validation et limites dans le TODO.

- [Raccourcis configurables](adr/preferences/hotkeys/0089-configurable-hotkeys.md) : catalogue Windows, brouillon et configuration commune au clavier et aux menus ; page Hotkeys N2, validations et limites dans le TODO.
