# ADR 0117 — Configuration Git avancée

Éditer les huit booléens Git Windows dans la même session que Config : pull.rebase, fetch.prune, merge.autostash, rebase.autostash/autosquash/updateRefs et rerere.enabled/autoupdate.

Local et Global sont modifiables ; Effective et System restent en lecture seule avec provenance. Une valeur inconnue, vide ou sans valeur reste intacte tant que l'utilisateur ne la change pas. Les trois intentions explicites sont true, false et suppression de clé. Le writer natif conserve comparaison optimiste, restrictions includes/multivaleurs et reçus des effets partiels.

Settings → Git → Advanced ouvre l'onglet correspondant du dialogue partagé, aussi en Global sans dépôt. updateRefs dépend de Git 2.38. Aucun deuxième store ou équivalent XML distribué. Repères : `GitConfigAdvancedSettingsPage`, `GitToolsDraftSession`, `GitConfigurationFields`.
