# ADR 0093 · Configurer les outils diff et merge

Statut : sous-périmètre vérifié par suites, paquet et natif T01 ciblé. Config reste N1.

Choisir les outils depuis Git → Config et Settings pour les consommateurs existants. Reprendre six champs Windows : nom, chemin et commande, pour Merge puis Diff. Les noms utilisent des ComboBox éditables avec flèche et catalogue ; valeurs inconnues et chemins Linux restent littéraux.

Effective/System sont readonly ; Local/Global gardent leurs brouillons. Changer de portée ne publie rien. OK/Apply publient explicitement ; Cancel abandonne les changements non publiés, sans rollback des écritures acquises. Browse annulé ne change rien ; Suggest explicite peut remplacer la commande personnalisée, sans lancer l’outil.

Conserver origines/includes, reçus natifs, effets partiels et readback disponible. Après annulation, ce dernier peut rester inconnu : ne pas inventer l’état effectif. Une include peut encore gagner après exit0 ; garder l’avertissement. Fermeture et annulation drainent les acteurs possédés, sans transaction globale ni CAS externe.

Git inférieur à 2.26 rend cette seule section outils indisponible à l’édition, avec diagnostic. Les autres réglages ne sont pas déclarés indisponibles.

Repères : `GitToolsSettingsDialog/Page/DraftSession/Catalog`, `GitRepository.ToolConfiguration`. Identité, éditeur, templates, credentials, encoding/autocrlf, découverte exhaustive et Global hors dépôt dans l’UI restent ouverts. [Full vérifié](../../../../../artifacts/linux/tools-config-20261018/full-01/parent-verification.json) ; paquet et natif T01 ciblé vérifiés. Treize familles ouvertes, aucun N4.
