# ADR 0034 · Sélecteur de branche courant

Statut : accepté pour le menu et le checkout ; migration globale encore ouverte.

Référence : upstream/master afc5123953fef0d128a9dd2ee3b3a682a234ac6e, `FormBrowse.CurrentBranchClick/CurrentBranchDropDownOpening/branchSelect_MouseUp`, `FormCheckoutBranch.DoDefaultActionOrShow` et `HotkeySettingsManager.CheckoutBranch`.

Nom et flèche ouvrent la même liste : Checkout branch… (Ctrl+.), séparateur, au plus 100 branches locales. Hors graphe : EyeClosed/couleur atténuée, mais sélection possible. Relire les refs à chaque ouverture ; tags/remotes et clic droit passent par le dialogue complet. Bare : liste consultable, actions désactivées.

Réutiliser CheckoutBranchDialog : checkout direct sur arbre propre ; arbre modifié ou AlwaysShow → dialogue présélectionné. Le défaut explicite pour les changements locaux reste distinct du choix de politique. Garder erreurs Git, stash, sous-modules, AutoCompile et annulation. Une branche disparue ne sélectionne jamais implicitement une autre branche.

Repères : `MainWindow.Branches`, `CheckoutBranchDialog`, `ReferencePreferencesStore`, tests/captures `artifacts/linux/branch-test-results`. Le [TODO](../../../todo.md) pointe le manifeste du paquet final et distingue validation isolée et installation hôte.
