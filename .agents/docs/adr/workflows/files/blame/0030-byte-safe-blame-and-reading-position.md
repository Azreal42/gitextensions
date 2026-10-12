# ADR 0030 · Attribution et position de lecture

Statut : accepté ; validation locale, paquet suivant à valider.

Besoin : ouvrir l’attribution à la ligne que le développeur lit déjà, quel que soit le codec de son fichier.

Décision : le contenu blame suit le codec configuré ou le choix de session ; les métadonnées Git restent UTF-8 strictes. Le choix de session ne modifie pas la configuration du dépôt. Browse et File History transmettent la ligne du document effectivement chargé. Même dépôt/fichier : conserver la position ; autre identité : repartir à la première ligne. Origine et prédécesseur utilisent leur remap explicite.

Une lecture échouée reste indisponible aux commandes du document ; une lecture réussie de texte inchangé doit libérer le lecteur sans déplacer sélection ou défilement.

Repères : `GitRepository.BlameHistory`, `BlameView`, `BlameDialog`, `FileHistoryDialog`, `DiffView`, `FileContentView` ; original `GitModule.ParseGitBlame`, `BlameControl` et `RevisionDiffControl`. Tests : `BlameEncodingScenarios`, `BlameEncodingPositionTests`, `FileEncodingViewTests`.
