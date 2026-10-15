# ADR 0059 · Fenêtre Pull/Fetch historique

## Besoin

Reprendre la capture Windows et FormPull : aide à gauche, Pull from, Branch, Merge options et Tag options à droite, puis pruning et boutons alignés en bas. Garder le vrai workflow Git et sa récupération.

## Décision

Utiliser les PNG HelpPull originaux dans OriginalCommandHelp : mode courant, variante fast-forward au survol de Merge et choix Show/Hide mémorisé. Conserver les menus de combo et les radios natifs ; surcharger localement les glyphes à 13 px. La légende coupe le filet du groupe. Les XLF de FormPull restent la source des libellés, avec adaptation du contraste du lien d’aide.

Remote et branche distante sont des combos éditables. URL montre la source du remote sélectionné et devient éditable avec son radio ; Browse utilise le sélecteur de dossier système. Manage remotes réutilise le dialogue existant et recharge les références/configurations réelles.

En Merge/Rebase, la branche locale affiche le checkout courant et reste désactivée. En Fetch, une destination explicite utilise un refspec de branche qualifié : valider les noms, refuser une branche checkoutée dans un worktree et confirmer le remplacement d’une branche existante. Sans destination, garder le fetch habituel. Les chemins/URL passent comme arguments Git littéraux ; l’historique partagé refuse déjà les URL contenant des secrets.

Garder Process, autostash, conflits, Continue/Abort et leurs effets natifs. Placer les actions de récupération hors de la zone défilante ; sortie et erreur peuvent défiler sans les recouvrir. Masquer les panneaux vides et fermer par Échap/croix, comme la référence.

Repères : PullDialog, PullDialog.Layout/Source, OriginalCommandHelp, PushRemoteDialog partagé, GitRepository.Network. Vérifications et limites dans le [TODO](../../../todo.md). L’audit natif étendu n’est pas clôturé par les rendus headless.
