# ADR 0087 — Recherche incrémentale dans la grille

Statut : accepté sur le périmètre décrit ci-dessous.

Retrouver une révision parmi les lignes affichées sans changer le filtre ni relancer la requête d’historique. Reprendre QuickSearchProvider et GitRevisionTester Windows : frappe directe, auteur/message/références même masquées, préfixe SHA de plus de deux caractères, boucle Alt+Haut/Bas et dernier terme conservé après Échap/expiration. Garder le dernier résultat trouvé si les caractères suivants ne correspondent plus ; un échec ne restaure pas la sélection de départ.

Conserver le vrai message `%B` séparément de Subject/Body : `%s` replie le premier paragraphe et `%b` exclut le sujet. La recherche ne réécrit pas les DTO des lecteurs historiques. Collage exact, frappe minuscule par char UTF16 en culture courante, auteur culturel et références/message ordinal ; aucune regex, note ou recherche de committer ajoutée.

Index/Working directory restent des destinations typées. Leurs identifiants Windows 222/111 servent seulement au prédicat et ne partent jamais vers Git. Browse ordinaire garde son panneau Working directory ; revenir à un commit par recherche restaure le panneau de révision précédent seulement si ce basculement est encore actif. Une paire initiale Index/WT→commit est réduite à une vraie sélection unique, même si la ListBox n’avait déjà qu’une ligne réelle.

Les lectures de détails restent annulables ; une réponse de presse-papiers tardive ne remplace ni une frappe ni une sélection plus récente, et la fermeture n’attend pas un callback natif non annulable. Aucun état global occupé pendant la recherche. L’aide et la notice utilisent les traductions, icône et palette originales.

Repères : `MainWindow.QuickSearch`, `Controls/RevisionQuickSearch`, `RevisionSearchButton`, `GitCommit.FullMessage`, `GitLogQuery/GitOutputParser`. Tests : `RevisionQuickSearchTests`, `QuickSearchMessageScenarios`. Browse demeure N2 ; placement des lignes artificielles et routes Linux restent adaptés, délai Windows de 4000ms sans page de réglage dédiée. Parcours parent/enfant réversible, cibles filtrées et audit natif étendu restent ouverts ; aucun N4.

Source `b7967dbcfc4126285cc438e516f6068bab95311d` : [suites complètes vérifiées](../../../../../artifacts/linux/revision-quick-search-20261017/full-01/parent-verification.json) et [paquet exact vérifié](../../../../../artifacts/linux/snapshots/b7967dbcf/proofs/package-parent-verification.json), **1534/1534 Desktop**, **669/669 Core**, **35/35 Plugins**. [Natif ciblé accepté](../../../../../artifacts/linux/snapshots/b7967dbcf/proofs/native-quick-search-parent-review.json) : une session anglais/clair sous X11 privé du payload exact ; seuls les gestes et frames du rapport font foi. Autres thèmes/locales, presse-papiers natif, Wayland/DPI/accessibilité et audit étendu restent ouverts.
