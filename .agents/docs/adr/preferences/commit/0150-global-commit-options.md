# ADR 0150 · Page globale Commit

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Rendre éditables ensemble les sept options Windows déjà consommées : completion, deuxième ligne vide, nombre de messages précédents, mémoire Amend et trois boutons additionnels. La page Detailed → Commit utilise les profils Commit et Desktop existants, sans nouvelle portée locale/distribuée. Les boutons partagent les vrais workflows Commit/Push/Reset.

Écrire uniquement les intentions éditées, conserver champs inconnus et réglages modifiés depuis une autre fenêtre ; acquitter chaque fichier accepté même si le suivant échoue. Cancel ne défait pas Apply. Les publications suivent les contrôleurs completion/dictionnaire existants et ne réécrivent pas la politique spelling d’une autre fenêtre. Ce contrôle optimiste n’est pas une transaction multi-fichiers ni un verrou interprocessus.

ShowErrorsWhenStagingFiles et UseFormCommitMessage=false restent des contrats Windows distincts : rapport automatique des erreurs de staging, ou délégation de l’édition à Git. Leur présence n’est pas déduite des sept options portées.
