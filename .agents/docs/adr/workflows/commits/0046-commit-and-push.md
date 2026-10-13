# ADR 0046 · Commit puis Push

Statut : sous-périmètre vérifié et livré sur `d144dcf69` ; limites dans le [TODO](../../../todo.md).

Besoin : Commit & Push réutilise le workflow Push complet et ses possibilités de récupération. Un commit réussi reste conservé si le push échoue ou est annulé. Les préférences originales de fermeture de Commit s’appliquent au succès du commit, indépendamment du résultat du push.

Force-with-lease après amend/soft-reset reste une préférence désactivée par défaut, capturée avant réinitialisation des contrôles. Le propriétaire et son token portent le Push et sa récupération Pull/sous-modules.

Repères : FormCommit.CommitClick, PushDialog.PushAndShowWhenFailedAsync, CommitDialog, CommitPreferencesStore.
