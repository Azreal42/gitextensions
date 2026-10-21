# ADR 0144 — Lire un historique ou un commit dans sa fenêtre

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.
Intention : ouvrir la lecture dédiée attendue par le geste Windows, sans confondre historique, commit et comparaison A/B.

Entrées : grille vide / CLI viewdiff → FormLog ; double clic commit réel → FormCommitDiff modeless ; aperçus Blame (autonome, FileHistory, inline Browse)/Rebase → modal CommitDiff.

Décision : modes MainWindowKind distincts partageant les vrais contrôleurs : Log750×529/grille205/fichiers188 ; CommitDiff717×529/CommitInfo200/fichiers260.

CommitDiff résout un OID fixe et ses parents effectifs, sans historique caché ; F5 relit ce même objet. FormDiff A/B garde son parcours.

Les sessions modeless dépendent de la vraie racine : un lanceur secondaire peut fermer ; la racine annule et draine leurs lectures.

Liens de révision désactivés dans ce CommitInfo comme Windows ; notes, Show all et liens externes restent réels.

Log/CommitDiff partagent le scroll Alt/option/délai, sans wrap ni relecture hors bornes, avec landing après publication.

Repères/tests source : [RevisionLogWindow](../../../../../src/linux/GitExtensions.Desktop/RevisionLogWindow.cs) ; [CommitDiffWindow](../../../../../src/linux/GitExtensions.Desktop/CommitDiffWindow.cs) ; [Log tests](../../../../../tests/linux/GitExtensions.Desktop.Tests/RevisionLogWindowTests.cs) ; [CommitDiff tests](../../../../../tests/linux/GitExtensions.Desktop.Tests/CommitDiffWindowTests.cs).

Validation Git/headless liée au TODO ; Headless avec lifetime réel ne qualifie pas la boucle native, tous les modes ou le paquet.

Show changes résout la ligne cliquée même hors grille ; le lecteur inline et ses côtés Commit/Index/WorkingTree restent en place. L’ancien onglet Changes du Blame autonome conserve son lecteur, sans parité supplémentaire déduite.

Complément de fermeture vérifié en Git/headless : Log readonly/F5 ne demande plus un rechargement global du parent ; le caret peut être désélectionné sans perdre son retour clavier. Un signal May conservateur suit seulement les propriétaires réels de même instance GitRepository et les reçus explicites des dialogues cachés ; il subsiste après échec/annulation. Toute publication vérifie encore dépôt, propriétaire et tokens. Le signal d’un CommitDiff modeless indépendant n’est pas retransmis à Browse par cette chaîne Owner : ce raccord reste explicitement ouvert.

Le replay de fermeture est partagé par instance entre RepositoryDialog et StartupRepositoryDialog : un descendant ayant différé OwnerWindowClosing conserve le vrai propriétaire, puis lui restitue sa demande après Closed et drainage. Un Cancel ordinaire du descendant ne ferme pas le parent. Les veto hors IsBusy, tels que le flush tracking de Remotes, gardent le même reçu ; aucun listener global ou fermeture forcée.

Le Browse qui possède Log dans son lease doit recevoir son vrai Closing avant le veto d’un descendant occupé. Pendant ce seul modal, sa policy OwnerWindowOnly laisse le token et WindowWork partagés posséder annulation/drainage ; la policy précédente est restaurée. Fermer Log seul ne propage pas une demande inventée vers Browse. Sur le thread UI, le callback transmet Close immédiatement avant le retour au demandeur ; hors UI il conserve le marshaling Dispatcher. Le drainage reste asynchrone et possédé.
