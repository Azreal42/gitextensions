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

Complément de fermeture vérifié en Git/headless : Log readonly/F5 ne demande plus un rechargement global du parent ; le caret peut être désélectionné sans perdre son retour clavier. Un signal May conservateur suit seulement les propriétaires réels de même instance GitRepository et les reçus explicites des dialogues cachés ; il subsiste après échec/annulation. Toute publication vérifie encore dépôt, propriétaire et tokens. Le raccord modeless conserve une publication distincte de la chaîne Owner.

Historique L27 : treize cas Git/headless qualifiaient le retour May après Closed + drain vers Browse. Ce timing ne décrit pas le raccord H28 ci-dessous.

Décision H28 : lier les sources Windows `LockableNotifier`/`ILockableNotifier`, dans un contexte partagé par le Browse exact, son Log et les CommitDiff indépendants ouverts depuis ces grilles. La fermeture de Log ne détruit pas ce contexte. Il n’existe pas de menu ouvrant un CommitDiff modeless depuis un autre CommitDiff.

May reste une admission d’effets possibles, même après erreur/annulation. La source réserve synchroniquement Notify sous verrou, libéré après son drainage réel ; Log garde le verrou de sa durée modale. Les demandes regroupées publient une relecture au seul Browse encore valide : même instance GitRepository, Owner capturé et vies actives. Aucun refresh Log, aucune notification créée par lecture/F5 ou fermeture readonly ; rouvrir le même chemin ne réabonne pas l’ancien contexte.

L’observateur possède une lease du Browse, jamais de sa source, et ne dépend pas de la publication. Celle-ci attend seulement le frontier RunAsync antérieur du receveur, pas son WindowWorkCompletion global. La racine continue de posséder les sessions modeless ; le retour de focus Browse attend sa publication sans imposer cette attente au lanceur Log.

Qualification H28 : 54/54 Desktop (cinq classes) et 42/42 Plugins vérifiés en Git/headless sur les octets commités `27c190a78` ; preuves et limites dans le TODO. Repère : `MainWindow.CommitDiffMutationPublication`, `ModelessCommitDiffMutationTests`, `RevisionLogMutationPublicationTests`. Aucun verdict natif/paquet ou de parité globale n’en découle.

Le replay de fermeture est partagé par instance entre RepositoryDialog et StartupRepositoryDialog : un descendant ayant différé OwnerWindowClosing conserve le vrai propriétaire, puis lui restitue sa demande après Closed et drainage. Un Cancel ordinaire du descendant ne ferme pas le parent. Les veto hors IsBusy, tels que le flush tracking de Remotes, gardent le même reçu ; aucun listener global ou fermeture forcée.

Le Browse qui possède Log dans son lease doit recevoir son vrai Closing avant le veto d’un descendant occupé. Pendant ce seul modal, sa policy OwnerWindowOnly laisse le token et WindowWork partagés posséder annulation/drainage ; la policy précédente est restaurée. Fermer Log seul ne propage pas une demande inventée vers Browse. Sur le thread UI, le callback transmet Close immédiatement avant le retour au demandeur ; hors UI il conserve le marshaling Dispatcher. Le drainage reste asynchrone et possédé.
