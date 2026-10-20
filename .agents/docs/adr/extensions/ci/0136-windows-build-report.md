# 0136 — Montrer le rapport du résultat CI affiché

Statut : accepté sur le [périmètre ciblé et ses preuves](../../../todo.md#rapport-ci-windows--périmètre-ciblé). Aucun niveau ni famille clos.

L’onglet Build report suit Windows : il est présent si ShowBuildResultPage est activé et si la première révision sélectionnée possède une URL de résultat. ShowInBuildReportTab=false choisit le lien Open report centré, traduit et activé explicitement dans le navigateur ; il ne supprime pas l’onglet. C’est le parcours de GitHub Actions, GitLab et Azure DevOps. Sans URL, sans sélection ou sans préférence, retirer la page plutôt que laisser un rapport vide. Les diagnostics CI restent visibles dans la barre de statut, notamment pour Azure sans URL.

Un seul résultat représentatif fournit statut, texte, tooltip, cible de cellule, menus, PR et rapport pour un SHA. Reprendre la règle BuildServerWatcher : StartDate maximal ; à date égale, la dernière observation nouvelle ou modifiée gagne (>=). Un poll identique ne réémet pas les anciennes observations et ne change pas leur priorité. Ne choisir ni le pire statut, ni UpdatedAt, ni le premier résultat doté d’une URL. Le dernier représentant sans URL ne retombe pas sur l’URL d’un ancien build.

Runs et groupes restent des données complètes ; une projection d’affichage distincte est calculée une fois à la publication et lue par SHA sans rescanner les runs. Chaque fournisseur garde ses admissions : GitHub Actions utilise StartedAt ?? CreatedAt ; l’observation Windows GitLab garde sa date par défaut null, sans inventer CreatedAt comme StartDate ; Jenkins part de ses statuts de commits admis, sans réinterpréter toute sa collecte. Changement de dépôt/configuration réinitialise observations et liens ; une réponse périmée ne ressuscite pas l’ancienne cible.

Browse/30 FocusBuildServerStatus, Ctrl+7 par défaut et configurable, sélectionne le rapport disponible et focalise son vrai contrôle. Sans rapport, il ne crée pas de page. Une publication CI ne vole pas le focus et ne remplace pas les objets commits. Les ouvertures externes gardent les validations d’URL existantes et exigent un geste.

Repères : Core BuildServerSnapshot.RevisionObservations et BuildServerRevisionProjection ; MainWindow.GitHubActions (publication, BuildServerStatusFor, rapport) et MainWindow.Hotkeys ; BuildReportParityTests et régressions des fournisseurs.

ShowInBuildReportTab=true conserve pour l’instant l’adaptation Linux en liste/bouton ; le rapport HTML Windows reste un parcours partiel à traiter séparément. Aucun viewer, jobs ou logs absent du contrat Windows GitHub n’est inventé. Le natif local GHA et le paquet exact ont leurs preuves dans le TODO ; les serveurs réels/authentifiés restent ouverts ; la dernière référence complète Desktop/Plugins reste ac59 ; le ciblé et le Core frais de 545f ont leurs preuves distinctes.
