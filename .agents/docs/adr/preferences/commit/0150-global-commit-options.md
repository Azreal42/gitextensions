# ADR 0150 · Page globale Commit

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Rendre éditables ensemble les neuf options Windows : completion, deuxième ligne vide, nombre de messages précédents, mémoire Amend, trois boutons additionnels, erreurs de staging et édition du message par Git. La page Detailed → Commit utilise les profils Commit et Desktop existants, sans nouvelle portée locale/distribuée. Les boutons partagent les vrais workflows Commit/Push/Reset.

Écrire uniquement les intentions éditées, conserver champs inconnus et réglages modifiés depuis une autre fenêtre ; acquitter chaque fichier accepté même si le suivant échoue. Cancel ne défait pas Apply. Les publications suivent les contrôleurs completion/dictionnaire existants et ne réécrivent pas la politique spelling d’une autre fenêtre. Ce contrôle optimiste n’est pas une transaction multi-fichiers ni un verrou interprocessus.

ShowErrorsWhenStagingFiles est lu au geste complet de staging : false ajoute seulement l’override temporaire safecrlf et supprime le rapport automatique. Un échec conserve son résultat et rafraîchit l’état sans rejouer la mutation. CommitStagingReportDialog reprend le rapport readonly, sans prétendre couvrir toutes les variantes de FormStatus.

UseFormCommitMessage est capturé à l’ouverture : false désactive le message du formulaire et délègue au vrai Git/éditeur par le terminal/helper existant, sous le verrou Core jusqu’au drainage. Les flags ordinaires et Amend HEAD sont conservés ; les types/cibles non-HEAD restent exclus de ce mode. Une erreur ou fermeture conserve les effets déjà écrits, sans faux succès ni Push/staging parent implicite. Le staging Linux garde add -A littéral ; macOS et parité générale FormStatus restent ouverts.
