# ADR 0039 · Options historiques de Pull

Statut : implémenté et contrôlé localement ; audit natif et paquet à renouveler dans le [TODO](../../../todo.md).

Besoin : retrouver les choix de tags, unshallow et autostash de FormPull. Suivre tagopt reste le défaut ; Fetch seul autorise tous les tags, Merge/Rebase rétablissent ce défaut. Unshallow concerne uniquement un dépôt shallow et reste un choix de session.

Autostash se déclenche sur les changements suivis avant Pull, sans confondre un sous-module modifié avec un fichier à stasher. Le réglage global distinct peut inclure les fichiers non suivis dans cette sauvegarde, mais des fichiers non suivis seuls ne la déclenchent jamais. Il ne remplace pas ce workflow par le flag Git --autostash : après succès sans opération en cours, proposer de réappliquer le stash avec un choix mémorisable. Échec/conflit conserve le stash à disposition.

Après Pull, réutiliser la décision et la mise à jour des sous-modules du Checkout, y compris AutoCompile. Aucun nouveau réglage de récursion ajouté au formulaire absent de l'original.

Repères : FormPull ; PullDialog, PullPreferencesStore, GitRepository.Network, CheckoutSubmodulePrompt, SubmoduleUpdateDialog, SubmoduleCheckoutPreferencesStore, MainWindow.Pull.
