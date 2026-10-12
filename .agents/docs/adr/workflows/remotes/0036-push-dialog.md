# ADR 0036 · Push natif

Statut : implémenté et testé localement ; paquet final à contrôler.

Besoin : retrouver FormPush et ses choix explicites de branches, tags et mappings multiples. La branche proposée vient du checkout actuel ; la sélection du graphe ne devient pas une source de push implicite.

Conserver la distinction remote/URL, source/destination, tracking, récursion des sous-modules et actions push/force/delete. Git garde ses règles de refspec, leases, hooks et authentification ; les valeurs sont passées comme arguments séparés. Les confirmations originales précèdent la mutation, avec reprise après rejet par Pull, Rebase ou Force with lease. Un refus de hook ou de permission ne justifie pas une proposition de force automatique.

Les préférences durables gardent récursion et confirmations, pas une force activée au prochain lancement. Clone et Push partagent l'historique des destinations. Relire le dépôt après toute tentative qui peut avoir changé les refs ou la configuration, y compris un rejet partiel ou la fermeture d'un dialogue enfant.

Repères : FormPush ; PushDialog, PushPreferencesStore, CloneSourcesStore, GitRepository.Push, GitPushRequest ; PushScenarios et PushDialogTests. Sortie Git continue et autres intégrations réseau restent dans l'audit général.
