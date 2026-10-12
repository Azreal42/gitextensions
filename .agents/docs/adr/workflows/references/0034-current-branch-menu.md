# ADR 0034 · Sélecteur de branche courant

Statut : accepté pour le menu et le checkout ; migration globale encore ouverte.

Référence : upstream/master afc5123953fef0d128a9dd2ee3b3a682a234ac6e, FormBrowse.CurrentBranchClick/CurrentBranchDropDownOpening/branchSelect_MouseUp, FormCheckoutBranch.DoDefaultActionOrShow et HotkeySettingsManager.CheckoutBranch.

Le contrôle Windows est un ToolStripSplitButton : le nom et la flèche ouvrent la même liste. Reprendre Checkout branch… (Ctrl+.), un séparateur et les 100 premières branches locales au plus. Les têtes absentes du graphe gardent une icône EyeClosed et une couleur atténuée, tout en restant sélectionnables. Les refs sont relues à chaque ouverture ; tags et remotes passent par le dialogue complet. Le clic droit ouvre aussi ce dialogue.

La sélection utilise le workflow CheckoutBranchDialog existant : checkout direct lorsque l’arbre est propre ; arbre modifié ou préférence AlwaysShow -> dialogue présélectionné. Le défaut explicite pour les changements locaux reste distinct du simple choix de politique. Conserver erreurs Git, stash, sous-modules, AutoCompile et annulation. Une branche disparue ne doit jamais sélectionner implicitement une autre branche. Les actions sont désactivées pour un dépôt bare, mais la liste reste consultable.

Validation ciblée : 11 scénarios Avalonia et vrais dépôts Git, incluant les deux thèmes, clic nom/flèche, raccourci dans le popup, clic droit, débordement, changements index/worktree, stash par défaut explicite, erreur worktree occupé, refs externes/limite 100, branche disparue, hors graphe, HEAD détaché, bare et unborn. Captures dans artifacts/linux/branch-test-results. La validation du paquet final est conservée séparément avec son SHA source et son checksum.
