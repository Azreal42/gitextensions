# ADR 0040 · Cherry-pick de commits

Statut : implémenté et contrôlé localement ; audit natif et paquet à renouveler dans le [TODO](../../../todo.md).

Besoin : retrouver FormCherryPick : choix de révision, parent numéroté d'un merge, création automatique du commit et ajout de sa référence. Les deux cases sont désactivées par défaut et mémorisées après acceptation ; les anciens appelants Core qui créent un commit gardent leur comportement.

Une sélection multiple ouvre un dialogue par commit, dans l'ordre inverse des lignes du graphe, en reprenant seulement les deux cases entre fenêtres. Annuler stoppe la suite sans annuler implicitement les commits déjà appliqués. La sélection entière est résolue avant le premier dialogue.

Le mode sans commit conserve l'index existant et les changements appliqués pour un commit ultérieur. Un conflit avec -n peut ne créer aucun CHERRY_PICK_HEAD : sa reprise conserve les fichiers résolus sans inventer un commit, et l'abandon doit restaurer la situation précédant la tentative. Le mode automatique utilise la reprise Git native.

La récupération inclut les cibles de renommage prévisibles et les changements suivis préexistants. Si Git redirige vers un chemin hors sauvegarde, refuser avant toute écriture et conserver l’état interrompu ; la récupération de ces déplacements de dossier reste à compléter. Ne pas lire les octets des jeux de données non suivis sans rapport avec le commit.

Repères : FormCherryPick, GitUICommands.StartCherryPickDialog ; GitRepository.CherryPick, CherryPickDialog, CherryPickSequence, MainWindow.Workflows.
