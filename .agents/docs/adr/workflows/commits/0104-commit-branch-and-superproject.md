# ADR 0104 · Branche Commit et index du superproject

Décision : reprendre les choix Windows ; preuves ciblées, natif source borné et paquet isolé restent distincts dans le [TODO](../../../todo.md#branche-et-superproject-depuis-commit--qualification-ciblée).

Commit réutilise Create branch et le raccourci configuré. Le statut branche→remote/merge suit les replis Windows ; le lien ouvre le tracking local sans recharger message, listes ou diff. Le nom initial suggéré respecte la saisie commencée.

HEAD détachée hors rebase propose Cancel par défaut, Checkout, Create ou Continue. Le checkout ciblé revalide le containment et la référence ; l’action par défaut partagée exige l’opt-in de cet hôte. Annulation/veto/refus arrêtent la tentative ; une erreur garde le même picker.

Stage in Superproject exige une association réelle. La préférence globale XDG, vraie par défaut, est publiée immédiatement et relue strictement après commit/push réussis. Seul le gitlink associé est indexé au HEAD enfant final, y compris après récupération/rebase, sans commit/push parent. Erreurs de profil, association, Git ou readback conservent effets et HasCommitted, sans fermeture automatique. Gardes ciblées, verrous Git et readback ne promettent aucune transaction globale contre un éditeur externe.

`CompactMenuToolbar` conserve les commandes canoniques, Options à droite et le débordement au chevron ; `ClassicCommandLink` partage les décisions Commit vide/Push/HEAD détachée. Repères : `CommitDialog.Branches/Superproject/Toolbar`, `CreateBranchDialog.SuggestInitialNameAsync`, `CommitPreferencesStore.LoadForSuperprojectStaging`, `GitRepository.Superproject` et `GitSuperprojectSnapshot`. Code et tests restent la source de vérité.
