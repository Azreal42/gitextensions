# ADR 0093 · Configurer les outils diff et merge

Statut : sous-périmètre vérifié à d975 par suites, paquet et natif T01 ciblé ; prolongé par [ADR 0094](../configuration/0094-git-configuration-and-encodings.md).

Choisir les outils depuis Git → Config et Settings pour les consommateurs existants. Reprendre six champs Windows : nom, chemin et commande, pour Merge puis Diff. Les noms utilisent des ComboBox éditables avec flèche et catalogue ; valeurs inconnues et chemins Linux restent littéraux.

Effective/System sont readonly ; Local/Global gardent leurs brouillons. Changer de portée ne publie rien. OK/Apply publient explicitement ; Cancel abandonne les changements non publiés, sans rollback des écritures acquises. Browse annulé ne change rien ; Suggest explicite peut remplacer la commande personnalisée, sans lancer l’outil.

Conserver origines/includes, reçus natifs, effets partiels et readback disponible. Après annulation, ce dernier peut rester inconnu : ne pas inventer l’état effectif. Une include peut encore gagner après exit0 ; garder l’avertissement. Fermeture et annulation drainent les acteurs possédés, sans transaction globale ni CAS externe.

Le lecteur à origines exige Git 2.26. Depuis ADR 0094, cette contrainte couvre la page Config guidée ; l’édition brute reste séparée.

Repères : `GitToolsSettingsDialog/Page/DraftSession/Catalog`, `GitRepository.ToolConfiguration`. Les sept autres champs et les encodages sont traités par ADR 0094. Découverte exhaustive et Global hors dépôt dans l’UI restent ouverts. [Full vérifié](../../../../../artifacts/linux/tools-config-20261018/full-01/parent-verification.json) ; paquet et natif T01 ciblé vérifiés. Treize familles ouvertes, aucun N4.
