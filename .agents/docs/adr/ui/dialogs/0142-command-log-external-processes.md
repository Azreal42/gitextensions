# ADR 0142 · Journaliser les processus externes

Le journal doit montrer les lancements externes intégrés à l’application, avec un descripteur neutre, le PID observé et la fin dans la même ligne. Le contexte pile/thread appelant est capturé avant les préparations asynchrones. Arguments, environnement, entrées, sorties et erreurs brutes restent hors du journal et de ses exports ; le cache explicite est distinct.

Une application détachée garde sa durée propre : annuler l’attente UI ou fermer son hôte ne la tue pas et ne finalise pas sa ligne. Scripts et builds conservent leur contrat de possession existant. La console interactive journalise le host authentifié, attend son drain avant de libérer la garde et distingue une erreur de protocole d’un code Git observé. Un handoff sans processus observable reste inconnu, sans succès ni échec inventé.

Source `61c883092983a58cfba053cdda28cd25adc0e6bd` : [preuves réelles et limites](../../../todo.md#processus-externes-du-journal). Build, ciblé, suites complètes, natif source, paquet isolé et inventaire ont des reçus distincts. L’interaction installée, les variantes natives étendues et les autres fenêtres restent ouvertes ; N2/aspect inchangés, treize familles, aucun N4. C3 conserve son périmètre historique.
