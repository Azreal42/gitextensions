# ADR 0080 — Menus de fichiers partagés

## Intention

Une feuille conserve ses propres côtés A/B, même quand le même chemin apparaît dans plusieurs groupes. Les commandes de Browse et Compare doivent garder cette provenance et le premier geste clavier après le menu.

## Décision

- Un constructeur/politique de menu, des opérations communes et des adaptateurs par hôte. Les callbacks réservés à Browse ne sont pas inventés dans FormDiff ; Range/Combined restent en lecture seule.
- Le diff suit la feuille sélectionnée même pendant le popup, sans reconstruire la liste. Le menu et la confirmation Reset décrivent les mêmes révisions réelles.
- Métadonnées Git distinctes des objets de diff, modes historiques lus sur les seuls chemins concernés ; aucune relecture complète d’arbre au clic. Le parent d’Index est explicite : aucun remplacement silencieux par HEAD courant. Reset partitionne les sources et refuse un même chemin avec deux sources contradictoires avant tout effet.
- Les outils reçoivent la paire de chaque feuille. Deux fichiers utilisent la feuille réellement focalisée comme côté B, indépendamment du premier SelectedItem ; le clic droit conserve la sélection multiple et le retour clavier vise cette même feuille. Le cache de focus est limité aux conteneurs et sélections encore présents. La mémoire est partagée entre fenêtres, liée au dépôt, sans modifier le renommage sélectionné. WT reste éditable ; Commit/Index utilisent des copies privées jusqu’à la fin de l’outil.
- Les dialogues communs et Commit prennent l’activation après mapping avant la première saisie. Une action changeant d’hôte, telle Show in File tree, remet le clavier à sa ligne cible après sélection/layout ; l’ancien panneau caché ne reprend pas le focus.
- Stage/Unstage ne visent que les états concernés ; les sous-modules utilisent leurs dépôts enfants revalidés. Une tentative susceptible de modifier le dépôt déclenche une relecture, y compris après échec partiel.
- Interactive add et Reset chunk utilisent un vrai terminal et les commandes Git originales (`add --patch`, `checkout --patch`). Un helper acquitté avant Git possède les processus, annule et attend leur fin ; il conserve un résultat Git déjà terminé.

## Adaptations et limites

Linux propose les chemins POSIX/natifs ; les conversions WSL/Cygwin sont spécifiques Windows. L’historique artificiel utilise la fenêtre Linux existante avec repli explicite vers le premier commit réel (branche dédiée Windows), tandis que le défaut Windows utilise Browse. Le candidat L25 transmet la paire gitlink capturée vers le Browse enfant, sans retargeter HEAD (ADR0147) ; qualification finale à relier. Découverte de tous les outils installés, scripts/Visual Studio, préférences correspondantes et audit complet des variantes restent ouverts. Browse/FormDiff restent N2.

## Validation

Sources, suites et périmètre natif : [TODO](../../todo.md). Les tests ciblés privés ne certifient pas le paquet ni une parité globale.

Complément CLI L25 : `searchfile [path]` réutilise FileListFindDialog sur le vrai HEAD. Accepter écrit seulement le chemin absolu littéral et sa newline sur stdout ; annuler/échouer garde stdout vide et renvoie -1. Aucun Browse ni ouverture de fichier ; lecteurs drainés. [SearchFileCommandLineTests](../../../../tests/linux/GitExtensions.Desktop.Tests/SearchFileCommandLineTests.cs) est une source de scénarios, pas un résultat exécuté.
