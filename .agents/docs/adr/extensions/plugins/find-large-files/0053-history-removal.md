# ADR 0053 · Retirer les gros fichiers de l’historique

Statut : implémenté ; 1186 tests réussis, build et candidat Debian vérifiés. Pilotes natifs clair/sombre multi-chemins validés ; tri, taille minimale et réglages réouverts en sombre. Erreurs/annulation et variantes de dépôt encore ouvertes. Non livré.

Besoin : retrouver l’analyse automatique et la grille Windows, choisir les chemins à retirer et voir les résultats réels de la réécriture.

Décision : une seule réécriture pour les chemins cochés, suivie de la purge historique seulement après réussite. Refuser une sélection vide ; conserver les sauvegardes existantes lorsque Git refuse le traitement. Les tags annotés gardent leurs commits originaux : ne pas promettre la disparition de tout blob encore référencé.

Conserver les effets partiels et les sorties après erreur ou annulation. Un refus de confirmation garde la sélection. La fermeture du parent attend l’arrêt du processus ; Abort seul garde le parent ouvert. Les réglages passent par le store de plugins existant.

Repères : `FindLargeFilesDialog`, `FindLargeFilesPlugin`, `GitRepository.HistoryRemoval`, `GitHistoryRemovalResult`, `GitProcessDialog`. Périmètre et preuves : [TODO](../../../../todo.md).
