# 0071 · Résoudre un conflit de sous-module

Statut : périmètre ciblé testé ; audit GUI natif complet ouvert.

Le développeur retrouve FormMergeSubmodule : Base/Local/Remote viennent des stages 1/2/3 du gitlink, Current du vrai dépôt enfant. Une version absente affiche « deleted ». Ne pas transformer un dossier absent ou non initialisé en résolution par suppression, ni utiliser le HEAD du superprojet découvert par remontée.

Open submodule lance un Browse indépendant, sans fermer l’assistant. Checkout Branch réutilise le dialogue commun avec l’intersection des branches contenant Local et Remote ; accepter add/add sans Base. Un candidat unique est présélectionné, sinon demander le choix. Revalider la branche affichée avant son checkout ; seul un workflow terminé avec succès permet ensuite le staging parent. LastResult seul ne suffit pas après un checkout partiellement terminé ou annulé.

Stage Current accepte le HEAD enfant choisi par le développeur, même sans lien d’ascendance avec Local/Remote. Capturer et revalider stages/index et identités ; Refresh ne met à jour que Current. Un seul add littéral doit laisser exactement ce commit en stage0, sans stages conflictuels. Ne pas reproduire le double staging Windows, la fermeture malgré erreur ni son activation Base/Remote incohérente.

Tous les accès fichier/sous-module passent par le routage partagé. Après retour, relire les conflits et conserver les effets natifs, même après erreur ou annulation ; aucun commit ni Continue implicite. Fermer un propriétaire annule puis draine ses lectures/processus/modal enfants avant de rejouer sa fermeture. Les relectures postmutation restent annulables.

Repères : SubmoduleConflictDialog.*, ConflictedPathDialog, CheckoutBranchConstraint/CheckoutBranchDialog, BrowseApplicationLauncher, GitRepository.SubmoduleConflict.*, GitSubmoduleConflictSnapshot. Original : FormMergeSubmodule, FormCheckoutBranch.GetContainsObjectIdBranches, FormResolveConflicts.ResolveItemConflictAsync.

Validation : SubmoduleConflictScenarios, SubmoduleConflictDialogTests et ConflictRoutingTests ; suites et paquet liés au SHA dans le TODO. Rendus headless clair/sombre/français inspectés. Traductions longues, DPI/accessibilité et audit GUI natif étendu restent ouverts ; aucun N4.
