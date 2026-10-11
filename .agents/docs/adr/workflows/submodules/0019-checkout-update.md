# ADR 0019 · Sous-modules après checkout

Statut : accepté.

Besoin : retrouver les sous-modules correspondant au nouveau HEAD sans imposer une mise à jour à chaque changement de branche.

Décision : proposer la mise à jour après un checkout réussi, ses changements locaux réappliqués et un HEAD effectivement différent. Reprendre les décisions mémorisées de l'original ; fermer la question ne mémorise aucune réponse. Exécuter la mise à jour dans un dialogue annulable, puis proposer AutoCompile uniquement après son succès. Fermer le propriétaire annule les processus avant de rendre la main.

Repères : `CheckoutBranchDialog`, `CheckoutSubmodulePrompt`, `SubmoduleCheckoutPreferencesStore`, `SubmoduleUpdateDialog`, `MainWindow.Plugins`. Scripts checkout et sortie continue restent ouverts.
