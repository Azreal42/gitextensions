# ADR 0056 · Clonage observable et acquittement

Statut : accepté localement sur `4ea105aeb` ; paquet et audit natif suivis dans le TODO.

Besoin : retrouver le workflow Windows de Clone, voir le transfert et décider de sa suite avec Keep/OK/Abort.

Décision : Process possédé par Clone, avec les octets de sortie du Git système `clone -v --progress`. Keep conserve le dialogue après réussite ; OK acquitte la fin. Après réussite Git, découverte du dépôt, MRU et proposition d'ouverture attendent l'acquittement réel et l'absence de demande d'arrêt.

Abort ou fermeture programmée annule et attend Git et ses acteurs identifiés, avec leurs sorties et effets partiels conservés. Clone mémorise son propriétaire avant le veto du Process ; après drainage, reprendre une seule fois la fermeture normale demandée, sans nouvel acquittement.

Les APIs CloneAsync existantes gardent leur contrat. Conserver distincts le résultat natif terminé et une erreur ultérieure de découverte ; celle-ci ne réécrit pas l'exit Git.

Repères : `GitRepository.CloneInit.cs`, `CloneDialog.cs`, `GitProcessDialog`, `CloneStreamingScenarios.cs`, `CloneProcessDialogTests.cs` ; original `FormClone`/`FormProcess`. Choix de clonage et suggestions : ADR 0010/0020. Limites et preuves : [TODO](../../../todo.md).
