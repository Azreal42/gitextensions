# ADR 0084 — Sockets privés avec un TMPDIR long

## Intention

Lancer Git et les commandes fichier interactives lorsque le répertoire temporaire choisi par l’utilisateur dépasse la capacité d’un socket Unix, sans modifier son environnement ni relâcher l’isolation des processus.

## Décision

- Partager `LinuxUnixSocketDirectory` entre `GitProcessLaunch` et `InteractiveFileConsoleLauncher`. Conserver le TMPDIR normal si le chemin complet du socket tient dans **107 octets UTF-8**, hors NUL ; sinon créer seulement le répertoire de contrôle sous `/tmp`.
- Allouer un répertoire unique avec `mkdtemp`, puis normaliser et vérifier son mode **0700**, y compris sous umask restrictif ou parent setgid. Garder socket et requête privés. Une autre erreur du TMPDIR choisi reste visible : aucun repli silencieux pour permission refusée ou parent absent.
- Ne supprimer que le répertoire possédé. Nettoyer après erreur de préparation comme après succès ; conserver l’authentification des pairs, l’acquittement avant Git et l’attente des vrais acteurs avant de libérer l’opération. Une console annulée après la fin de Git conserve son résultat natif.

## Repères et portée

`LinuxUnixSocketDirectory`, `GitProcessLaunch`, `InteractiveFileConsoleLauncher`, `UnixSocketDirectoryScenarios` et `InteractiveFileConsoleSocketTests`. Cette adaptation Linux concerne ces deux lanceurs. Niveaux de portage inchangés, treize familles ouvertes, aucun N4.

## Validation

Source `dc85753b178daad3f8bbf5ecd1b10adbce26c70d` : [suites complètes](../../../../../artifacts/linux/unix-socket-temp-20261017/full-01/parent-verification.json), **1406/1406 Desktop**, **636/636 Core**, **35/35 Plugins**, sans retrait de cas anciens. Les quatre nouveaux tests console passent aussi dans deux testhosts neufs sous TMPDIR longs ASCII et UTF-8 ; ces répétitions ne s’ajoutent pas aux suites complètes.

[Paquet exact vérifié](../../../../../artifacts/linux/snapshots/dc85753b1/proofs/package-parent-verification.json), installation DPKG isolée ; [revue indépendante des processus et ouvertures X11](../../../../../artifacts/linux/snapshots/dc85753b1/proofs/unix-socket-native-parent-review.json) : 21 processus neufs, 13 succès et 8 annulations après démarrage, sockets de repli supprimés et contenu du paquet conservé. Ouverture du dépôt sous Xvfb avec TMPDIR de 128 et 148 octets. Le [rouge antérieur](../../../../../artifacts/linux/unix-socket-temp-20261017/probe/campaigns/b356-full-matrix/summary.json) est conservé séparément.

Aucun nouvel audit visuel ou des menus/focus, aucune installation hôte ni push. Wayland, DPI et accessibilité restent ouverts ; le code reste la source de vérité.
